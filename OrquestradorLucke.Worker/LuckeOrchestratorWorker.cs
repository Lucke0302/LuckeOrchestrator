using System.Text;
using Microsoft.Extensions.Options;
using OrquestradorLucke.Application.Configuration;
using OrquestradorLucke.Application.Interfaces;
using OrquestradorLucke.Application.Services;
using OrquestradorLucke.Domain;
using OrquestradorLucke.Worker.Configuration;

namespace OrquestradorLucke.Worker;

/// <summary>
/// Laço principal do orquestrador. Cada iteração abre um escopo próprio de DI — para que nenhuma
/// dependência (DbContext, adapters, Typed Clients) carregue estado entre execuções — e percorre o
/// ciclo completo: indexação do RAG, dequeue da fila, recuperação do contexto da própria base de
/// código, roteamento MoE da complexidade, geração dos arquivos pelo expert (um JSON estrito
/// caminho → conteúdo), sumarização da descrição do pull request e entrega da branch/commit/PR pela
/// conta de agente autônomo.
/// </summary>
/// <remarks>
/// Falhas de geração alimentam o <see cref="FrustrationTracker"/> do daemon: cada motivo é guardado
/// no histórico e, ao atingir <c>Frustration:MaxFailures</c>, o circuito desarma — a tarefa ganha uma
/// última tentativa no modelo mais robusto do catálogo
/// (<see cref="ITaskRouter.ResolveOverdriveProvider"/>) acompanhada do histórico de erros, para que o
/// expert maior não repita o que o menor errou. Só então a tarefa é marcada como <c>Falhou</c>.
/// </remarks>
public sealed class LuckeOrchestratorWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<LuckeOrchestratorWorker> logger) : BackgroundService
{
    /// <summary>Prefixo da branch criada para a tarefa (ex.: <c>feat/task-{id}</c>).</summary>
    private const string BranchNamePrefix = "feat/task-";

    /// <summary>Documentos de referência que o RAG entrega ao expert por tarefa.</summary>
    private const int SimilarDocumentsLimit = 3;

    /// <summary>
    /// Limite de caracteres do conteúdo de cada arquivo embutido no prompt de sumarização do pull
    /// request: o resumo descreve a mudança, não precisa do código inteiro.
    /// </summary>
    private const int MaxSummaryContentLengthPerFile = 2000;

    /// <summary>Instante (UTC) do último ciclo de indexação concluído — governa a cadência configurada.</summary>
    private DateTimeOffset _lastIndexingAtUtc = DateTimeOffset.MinValue;

    /// <summary>
    /// Medidor de frustração do daemon: acumula as falhas de geração entre iterações e é zerado no
    /// primeiro sucesso (o circuito volta a armar). Vive no campo do <see cref="BackgroundService"/>
    /// — e não no escopo da iteração — porque cada tarefa tem uma única tentativa por ciclo: um
    /// contador local nunca alcançaria <c>Frustration:MaxFailures</c> e o overdrive jamais dispararia.
    /// </summary>
    private FrustrationTracker? _frustrationTracker;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = scopeFactory.CreateScope();

            var services = scope.ServiceProvider;

            // Serviços Scoped resolvidos DENTRO do escopo da iteração — nunca injetados no construtor
            // do BackgroundService, o que prenderia o DbContext ao tempo de vida do host (memory leak).
            var taskRepository = services.GetRequiredService<IAgentTaskRepository>();
            var taskRouter = services.GetRequiredService<ITaskRouter>();
            var gitHubService = services.GetRequiredService<IGitHubService>();
            var codeIndexer = services.GetRequiredService<CodebaseIndexerService>();
            var codeContextRepository = services.GetRequiredService<ICodeContextRepository>();
            var embeddingProvider = services.GetRequiredService<IEmbeddingProvider>();

            var options = services.GetRequiredService<IOptions<OrchestratorWorkerOptions>>().Value;
            var frustrationSettings = services.GetRequiredService<IOptions<FrustrationSettings>>().Value;

            // RAG: mantém o índice vetorial da base de código atualizado antes de processar a fila.
            await TryIndexCodebaseAsync(codeIndexer, options, stoppingToken).ConfigureAwait(false);

            // Dequeue: a tarefa Pendente mais antiga já retorna deste método com status EmExecucao.
            var task = await taskRepository
                .GetNextPendingTaskAsync(stoppingToken)
                .ConfigureAwait(false);

            if (task is null)
            {
                // Fila vazia: aguarda a cadência configurada e volta para o topo do laço.
                await Task.Delay(
                        TimeSpan.FromSeconds(Math.Max(1, options.PollingIntervalSeconds)),
                        stoppingToken)
                    .ConfigureAwait(false);

                continue;
            }

            // Medidor de frustração compartilhado pelo daemon: cada falha de geração (retorno sem
            // arquivos, JSON estrito inválido do modelo) aproxima o circuito do overdrive.
            var frustrationTracker = _frustrationTracker ??= new FrustrationTracker(Math.Max(1, frustrationSettings.MaxFailures));

            try
            {
                // Cérebro (MoE): o roteador decide o expert pela complexidade e pela cota ativa de cada modelo.
                var provider = taskRouter.ResolveProvider(task.Complexidade);

                logger.LogInformation(
                    "Tarefa {TaskId} reivindicada: complexidade {Complexidade} roteada para o expert '{ModelName}'.",
                    task.Id,
                    task.Complexidade,
                    provider.ModelName);

                // RAG: o payload é embutido, os documentos mais próximos são recuperados do índice
                // vetorial e o contexto montado ("Arquivos de referência: ...") acompanha a geração.
                var contextAnalysis = await BuildRagContextAsync(
                        embeddingProvider,
                        codeContextRepository,
                        task.Payload,
                        stoppingToken)
                    .ConfigureAwait(false);

                // Uma tentativa de geração: falha (exceção do provedor ou retorno sem arquivos) já
                // incrementa o medidor de frustração e devolve null, sem derrubar a iteração.
                var artifacts = await TryGenerateArtifactsAsync(
                        provider,
                        task,
                        contextAnalysis,
                        frustrationTracker,
                        stoppingToken)
                    .ConfigureAwait(false);

                // Circuito desarmado: o limite de falhas foi atingido, então a tarefa ganha uma última
                // tentativa no expert mais robusto do catálogo antes de ser dada como perdida.
                if (artifacts is null && frustrationTracker.OverdriveDisparado)
                {
                    var overdriveProvider = taskRouter.ResolveOverdriveProvider();

                    // Memória da frustração: o histórico de motivos entra no parâmetro de contexto
                    // junto com o RAG, para o modelo mais robusto não repetir o erro do expert menor.
                    var overdriveContext = ComposeOverdriveContext(
                        contextAnalysis,
                        BuildFailureHistory(frustrationTracker));

                    logger.LogWarning(
                        "Ativando Overdrive na tarefa {TaskId}: {Falhas} falha(s) acumulada(s) no limite de {Limite}; escalando para o expert '{ModelName}' com {Historico} erro(s) de contexto.",
                        task.Id,
                        frustrationTracker.ContadorAtual,
                        frustrationTracker.LimiteMaximo,
                        overdriveProvider.ModelName,
                        frustrationTracker.HistoricoFalhas.Count);

                    artifacts = await TryGenerateArtifactsAsync(
                            overdriveProvider,
                            task,
                            overdriveContext,
                            frustrationTracker,
                            stoppingToken)
                        .ConfigureAwait(false);

                    if (artifacts is not null)
                    {
                        provider = overdriveProvider;
                    }
                }

                if (artifacts is null)
                {
                    // Falhou no expert da cadeia e, quando o overdrive disparou, também no modelo mais
                    // robusto: só então a tarefa é encerrada neste ciclo (um sucesso zera o medidor).
                    await UpdateStatusAsync(taskRepository, task, AgentTaskStatus.Falhou, stoppingToken)
                        .ConfigureAwait(false);

                    continue;
                }

                // Braços (GitHub): branch de trabalho, commit dos arquivos gerados e pull request.
                var branchName = $"{BranchNamePrefix}{task.Id}";

                logger.LogInformation(
                    "Entrega da tarefa {TaskId}: criando a branch '{Branch}' com {FileCount} arquivo(s) do expert '{ModelName}'.",
                    task.Id,
                    branchName,
                    artifacts.Count,
                    provider.ModelName);

                try
                {
                    await gitHubService.CreateBranchAsync(branchName, stoppingToken).ConfigureAwait(false);

                    // O expert devolve um JSON caminho → conteúdo: o dicionário segue direto para o commit,
                    // sem caminho de artefato fixo (uma tarefa pode entregar quantos arquivos precisar).
                    await gitHubService
                        .CommitChangesAsync(
                            branchName,
                            $"feat(task-{task.Id}): entrega automática do orquestrador",
                            artifacts,
                            stoppingToken)
                        .ConfigureAwait(false);

                    // Descrição do PR: sumarizada por um expert rápido a partir da tarefa e dos artefatos.
                    var pullRequestDescription = await BuildPullRequestSummaryAsync(
                            taskRouter,
                            task,
                            artifacts,
                            stoppingToken)
                        .ConfigureAwait(false);

                    var pullRequestUrl = await gitHubService
                        .OpenPullRequestAsync(
                            branchName,
                            $"feat(task-{task.Id})",
                            pullRequestDescription,
                            stoppingToken)
                        .ConfigureAwait(false);

                    frustrationTracker.RegistrarSucesso();

                    // AgentTask é um record imutável: o estado final é derivado por "with".
                    var concludedTask = task with
                    {
                        Status = AgentTaskStatus.Concluida,
                        AtualizadoEm = DateTimeOffset.UtcNow,
                        Branch = branchName,
                        PullRequestUrl = pullRequestUrl
                    };

                    await taskRepository.UpdateTaskAsync(concludedTask, stoppingToken).ConfigureAwait(false);

                    logger.LogInformation(
                        "Tarefa {TaskId} concluída na branch '{Branch}' pelo expert '{ModelName}' ({FileCount} arquivo(s)). Pull request: {PullRequestUrl}.",
                        task.Id,
                        branchName,
                        provider.ModelName,
                        artifacts.Count,
                        pullRequestUrl);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Desligamento do host no meio da entrega: o catch do laço devolve a tarefa à fila.
                    throw;
                }
                catch (QuotaExhaustedException)
                {
                    // Cota do expert de sumarização: tratamento do laço (cooldown + devolução à fila).
                    throw;
                }
                catch (Exception ex)
                {
                    // Fim do silêncio: nenhuma falha do GitHub é engolida. O erro é logado, alimenta a
                    // memória de frustração (histórico entregue ao overdrive) e a tarefa é marcada como
                    // Falhou — a branch eventualmente criada permanece no repositório para auditoria.
                    RegisterDeliveryFailure(provider, task, frustrationTracker, branchName, ex);

                    await UpdateStatusAsync(taskRepository, task, AgentTaskStatus.Falhou, stoppingToken)
                        .ConfigureAwait(false);

                    continue;
                }
            }
            catch (QuotaExhaustedException ex)
            {
                var cooldown = TimeSpan.FromMinutes(Math.Max(1, options.QuotaCooldownMinutes));

                // Cadeia MoE inteira bloqueada pelo Circuit Breaker: a tarefa volta para a fila e o
                // worker dorme para desestressar o rate limit de todo o provedor.
                logger.LogWarning(
                    ex,
                    "Cota esgotada no modelo '{ModelName}' (disponível em {AvailableAtUtc}). " +
                    "Tarefa {TaskId} devolvida para a fila; cooldown de {Cooldown}.",
                    ex.ModelName,
                    ex.AvailableAtUtc,
                    task.Id,
                    cooldown);

                await UpdateStatusAsync(taskRepository, task, AgentTaskStatus.Pendente, stoppingToken)
                    .ConfigureAwait(false);

                await Task.Delay(cooldown, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Desligamento do host no meio do processamento: a tarefa volta para a fila, pois o
                // dequeue só reivindica Pendentes (senão ficaria presa em EmExecucao). Grava com
                // CancellationToken.None porque o token do host já está cancelado.
                logger.LogInformation(
                    "Desligamento solicitado: tarefa {TaskId} devolvida para a fila.",
                    task.Id);

                await UpdateStatusAsync(taskRepository, task, AgentTaskStatus.Pendente, CancellationToken.None)
                    .ConfigureAwait(false);

                return;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Falha inesperada no processamento da tarefa {TaskId}; marcada como {Status}.",
                    task.Id,
                    AgentTaskStatus.Falhou);

                await UpdateStatusAsync(taskRepository, task, AgentTaskStatus.Falhou, stoppingToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Executa uma tentativa de geração com o expert informado, convertendo falha em frustração.
    /// </summary>
    /// <remarks>
    /// A exceção não é propagada de propósito: o worker decide o que fazer com a falha (nova
    /// tentativa no modelo mais robusto, no overdrive, ou encerramento da tarefa). Cota esgotada e
    /// desligamento do host continuam propagando, pois não são frustração do expert — são tratados
    /// pelos <c>catch</c> específicos do laço (devolução da tarefa à fila + cooldown).
    /// </remarks>
    /// <returns>Artefatos por caminho, ou <c>null</c> quando a tentativa falhou.</returns>
    private async Task<IReadOnlyDictionary<string, string>?> TryGenerateArtifactsAsync(
        ILLMProvider provider,
        AgentTask task,
        string contextAnalysis,
        FrustrationTracker frustrationTracker,
        CancellationToken stoppingToken)
    {
        try
        {
            var artifacts = await provider
                .GenerateCodeAsync(task.Payload, contextAnalysis, stoppingToken)
                .ConfigureAwait(false);

            // Auditoria do parse: quantos arquivos sobreviveram à desserialização do JSON do LLM — e
            // quantos têm conteúdo de fato. O número entra no log antes de qualquer efeito no GitHub.
            var deliverable = SelectDeliverableArtifacts(artifacts);

            logger.LogInformation(
                "Parse do expert '{ModelName}' na tarefa {TaskId}: {Usable} de {Total} arquivo(s) aproveitável(is).",
                provider.ModelName,
                task.Id,
                deliverable.Count,
                artifacts.Count);

            if (deliverable.Count == 0)
            {
                // Erro explícito (em vez de retorno vazio silencioso): cai no catch abaixo, alimenta a
                // memória de frustração e a tarefa termina como Falhou — após o overdrive, se disparar.
                throw new InvalidOperationException(
                    $"O expert '{provider.ModelName}' não devolveu nenhum arquivo com conteúdo para a tarefa {task.Id}.");
            }

            return deliverable;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (QuotaExhaustedException)
        {
            // Cota não é falha do expert: o Circuit Breaker de cota e o fallback do roteador MoE
            // tratam o caso, e a tarefa volta para a fila sem consumir tentativa do overdrive.
            throw;
        }
        catch (Exception ex)
        {
            // JSON estrito inválido (retorno que não é o mapa caminho → conteúdo), erro de protocolo
            // do provedor etc.: falha do expert, contabilizada para a escalada de modelo.
            RegisterGenerationFailure(provider, task, frustrationTracker, "resposta não parseável", ex);

            return null;
        }
    }

    /// <summary>
    /// Filtra o retorno do expert antes da entrega: só entram no commit os arquivos com caminho e
    /// conteúdo reais. Entrada em branco geraria arquivo vazio no repositório (e poluiria o diff do
    /// pull request), então o parse devolve apenas o que é publicável — e zero arquivos é falha.
    /// </summary>
    private static Dictionary<string, string> SelectDeliverableArtifacts(IReadOnlyDictionary<string, string> artifacts)
    {
        var deliverable = new Dictionary<string, string>(artifacts.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var artifact in artifacts)
        {
            if (string.IsNullOrWhiteSpace(artifact.Key) || string.IsNullOrWhiteSpace(artifact.Value))
            {
                continue;
            }

            deliverable[artifact.Key] = artifact.Value;
        }

        return deliverable;
    }

    /// <summary>
    /// Incrementa o medidor de frustração e registra a falha da tentativa, informando se o circuito
    /// desarmou (overdrive) na chamada.
    /// </summary>
    private void RegisterGenerationFailure(
        ILLMProvider provider,
        AgentTask task,
        FrustrationTracker frustrationTracker,
        string reason,
        Exception? exception = null)
    {
        // Motivo registrado no histórico: modelo + razão + mensagem da exceção quando houver. É esse
        // texto que o overdrive recebe como contexto para não repetir a mesma falha.
        var failure = exception is null
            ? $"{provider.ModelName}: {reason}"
            : $"{provider.ModelName}: {reason} ({exception.Message})";

        var overdriveTriggered = frustrationTracker.RegistrarFalha(failure);

        logger.LogWarning(
            exception,
            "Falha de geração do expert '{ModelName}' na tarefa {TaskId} ({Reason}): falha {Falhas}/{Limite} (overdrive: {Overdrive}).",
            provider.ModelName,
            task.Id,
            reason,
            frustrationTracker.ContadorAtual,
            frustrationTracker.LimiteMaximo,
            overdriveTriggered);
    }

    /// <summary>
    /// Contabiliza uma falha de <b>entrega</b> no GitHub (branch, commit ou pull request): registra o
    /// erro no log, alimenta a memória de frustração com o passo que falhou e deixa a decisão de
    /// status para o chamador — a tarefa segue para <c>Falhou</c> em vez de desaparecer sem entrega.
    /// </summary>
    /// <remarks>
    /// O motivo entra no <see cref="FrustrationTracker"/> pela mesma porta das falhas de geração, para
    /// o overdrive receber o histórico completo quando o circuito desarmar. A entrega não é repetida no
    /// mesmo ciclo: os artefatos já foram gerados e o problema é do repositório, não do modelo.
    /// </remarks>
    private void RegisterDeliveryFailure(
        ILLMProvider provider,
        AgentTask task,
        FrustrationTracker frustrationTracker,
        string branchName,
        Exception exception)
    {
        var failure =
            $"{provider.ModelName}: falha de entrega no GitHub na branch '{branchName}' ({exception.GetType().Name}: {exception.Message})";

        var overdriveTriggered = frustrationTracker.RegistrarFalha(failure);

        logger.LogError(
            exception,
            "Falha de entrega no GitHub para a tarefa {TaskId} na branch '{Branch}' (expert '{ModelName}'): falha {Falhas}/{Limite} (overdrive: {Overdrive}).",
            task.Id,
            branchName,
            provider.ModelName,
            frustrationTracker.ContadorAtual,
            frustrationTracker.LimiteMaximo,
            overdriveTriggered);
    }

    /// <summary>
    /// Formata o histórico de falhas do medidor na instrução de contexto entregue ao expert mais
    /// robusto no overdrive: o modelo maior recebe explicitamente o que o menor errou.
    /// </summary>
    private static string BuildFailureHistory(FrustrationTracker frustrationTracker)
    {
        var builder = new StringBuilder("ATENÇÃO: Tentativas anteriores falharam. Evite os seguintes erros:");

        foreach (var failure in frustrationTracker.HistoricoFalhas)
        {
            builder
                .AppendLine()
                .Append("- ")
                .Append(failure);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Concatena o contexto do RAG com o histórico de falhas, descartando as partes vazias — o expert
    /// recebe os documentos de referência e, abaixo deles, os erros a evitar.
    /// </summary>
    private static string ComposeOverdriveContext(string ragContext, string failureHistory)
        => string.IsNullOrWhiteSpace(ragContext)
            ? failureHistory
            : $"{ragContext}{Environment.NewLine}{Environment.NewLine}{failureHistory}";

    /// <summary>
    /// Persiste a transição de status preservando os demais campos do record (payload, branch, PR).
    /// </summary>
    private static Task UpdateStatusAsync(
        IAgentTaskRepository taskRepository,
        AgentTask task,
        AgentTaskStatus status,
        CancellationToken cancellationToken)
        => taskRepository.UpdateTaskAsync(
            task with { Status = status, AtualizadoEm = DateTimeOffset.UtcNow },
            cancellationToken);

    /// <summary>
    /// Corpo padrão do pull request: identifica a tarefa de origem e reproduz o payload recebido.
    /// Usado quando a sumarização pelo expert não produz texto.
    /// </summary>
    private static string BuildPullRequestDescription(AgentTask task)
        => $"Entrega automática da tarefa `{task.Id}` pelo orquestrador Lucke."
            + $"{Environment.NewLine}{Environment.NewLine}**Payload original**"
            + $"{Environment.NewLine}{task.Payload}";

    /// <summary>
    /// Mantém o índice vetorial do RAG em dia respeitando a cadência configurada.
    /// </summary>
    /// <remarks>
    /// Best-effort por dois motivos: (1) GitHub, banco ou provedor de embeddings indisponíveis não
    /// podem derrubar o laço — o índice atual continua servindo de contexto; (2) a leitura da árvore
    /// do repositório é cara em cota da API do GitHub, por isso o ciclo é espaçado e não roda a cada
    /// polling.
    /// </remarks>
    private async Task TryIndexCodebaseAsync(
        CodebaseIndexerService codeIndexer,
        OrchestratorWorkerOptions options,
        CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, options.IndexingIntervalMinutes));

        if (DateTimeOffset.UtcNow - _lastIndexingAtUtc < interval)
        {
            return;
        }

        try
        {
            var result = await codeIndexer
                .IndexChangedDocumentsAsync(stoppingToken)
                .ConfigureAwait(false);

            _lastIndexingAtUtc = DateTimeOffset.UtcNow;

            logger.LogInformation(
                "Índice do RAG sincronizado: {Indexed} documento(s) novos/alterados entre {Discovered} arquivo(s) C#{Discarded}.",
                result.IndexedDocuments,
                result.DiscoveredFiles,
                result.SkippedDocuments > 0
                    ? $"; {result.SkippedDocuments} descartado(s) por não gerarem embedding"
                    : string.Empty);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A cadência também é respeitada em caso de falha: sem isso um repositório inacessível
            // seria tentado a cada polling, multiplicando o erro e a cota consumida.
            _lastIndexingAtUtc = DateTimeOffset.UtcNow;

            logger.LogWarning(ex, "Falha ao indexar a base de código; o índice atual será reutilizado.");
        }
    }

    /// <summary>
    /// Monta o contexto do RAG para a tarefa: embute o payload, busca os documentos mais próximos no
    /// índice vetorial e concatena os arquivos de referência.
    /// </summary>
    /// <returns>
    /// Contexto formatado (<c>Arquivos de referência:</c> + conteúdo) ou string vazia quando o índice
    /// não devolve referências — caso em que o expert recebe a tarefa sem enriquecimento.
    /// </returns>
    private async Task<string> BuildRagContextAsync(
        IEmbeddingProvider embeddingProvider,
        ICodeContextRepository codeContextRepository,
        string payload,
        CancellationToken stoppingToken)
    {
        try
        {
            var queryEmbedding = await embeddingProvider
                .GenerateEmbeddingAsync(payload, stoppingToken)
                .ConfigureAwait(false);

            if (queryEmbedding.IsEmpty)
            {
                return string.Empty;
            }

            var documents = await codeContextRepository
                .SearchSimilarAsync(queryEmbedding, SimilarDocumentsLimit, stoppingToken)
                .ConfigureAwait(false);

            if (documents.Count == 0)
            {
                return string.Empty;
            }

            var context = new StringBuilder("Arquivos de referência:");

            foreach (var document in documents)
            {
                context
                    .AppendLine()
                    .AppendLine()
                    .Append("### ")
                    .AppendLine(document.FilePath)
                    .AppendLine(document.Content);
            }

            logger.LogInformation(
                "RAG: {Count} arquivo(s) de referência recuperados para a tarefa.",
                documents.Count);

            return context.ToString();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // O contexto é enriquecimento, não pré-requisito da tarefa: sem RAG a geração prossegue
            // apenas com o payload, em vez de forçar a tarefa de volta para a fila.
            logger.LogWarning(ex, "Não foi possível recuperar o contexto do RAG; seguindo sem referências.");

            return string.Empty;
        }
    }

    /// <summary>
    /// Gera a descrição do pull request com um expert rápido (complexidade baixa): sumarização não
    /// exige raciocínio pesado e a cota consumida aqui não disputa a cadeia principal da tarefa.
    /// </summary>
    /// <remarks>
    /// Falhas do sumarizador são absorvidas de propósito: a entrega já foi commitada, e devolver a
    /// tarefa para a fila (como faria o tratamento de cota do laço) recriaria branch/commit no ciclo
    /// seguinte. Sem resumo — inclusive quando o expert devolve algo que não é o JSON de arquivos e
    /// o parse falha —, o corpo do PR recebe a descrição determinística.
    /// </remarks>
    private async Task<string> BuildPullRequestSummaryAsync(
        ITaskRouter taskRouter,
        AgentTask task,
        IReadOnlyDictionary<string, string> artifacts,
        CancellationToken stoppingToken)
    {
        try
        {
            var summaryProvider = taskRouter.ResolveProvider(TaskComplexity.Baixo);

            var summaryArtifacts = await summaryProvider
                .GenerateCodeAsync(BuildPullRequestSummaryPrompt(task, artifacts), string.Empty, stoppingToken)
                .ConfigureAwait(false);

            // O expert responde sob o mesmo contrato JSON da geração (caminho → conteúdo), então o
            // resumo é o texto útil devolvido — concatenado quando veio distribuído em mais de um valor.
            var summary = string.Join(
                Environment.NewLine,
                summaryArtifacts.Values.Where(value => !string.IsNullOrWhiteSpace(value)));

            return string.IsNullOrWhiteSpace(summary)
                ? BuildPullRequestDescription(task)
                : summary.Trim();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Não foi possível sumarizar o Pull Request da tarefa {TaskId}; usando a descrição padrão.",
                task.Id);

            return BuildPullRequestDescription(task);
        }
    }

    /// <summary>
    /// Prompt de sumarização enviado ao expert rápido para o corpo do pull request: traz a tarefa e os
    /// artefatos gerados, com o conteúdo de cada arquivo limitado por
    /// <see cref="MaxSummaryContentLengthPerFile"/> — o resumo descreve a mudança, não o código inteiro.
    /// </summary>
    private static string BuildPullRequestSummaryPrompt(AgentTask task, IReadOnlyDictionary<string, string> artifacts)
    {
        var builder = new StringBuilder()
            .Append("Crie um resumo curto em texto puro para a descrição de um Pull Request que implementou esta tarefa: ")
            .Append(task.Payload)
            .AppendLine()
            .Append("Arquivos gerados:");

        foreach (var artifact in artifacts)
        {
            var content = artifact.Value.Length > MaxSummaryContentLengthPerFile
                ? artifact.Value[..MaxSummaryContentLengthPerFile]
                : artifact.Value;

            builder
                .AppendLine()
                .AppendLine()
                .Append("### ")
                .AppendLine(artifact.Key)
                .Append(content);
        }

        return builder.ToString();
    }
}
