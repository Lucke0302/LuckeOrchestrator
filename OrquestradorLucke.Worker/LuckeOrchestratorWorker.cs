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
/// código, roteamento MoE da complexidade, geração do artefato pelo expert, sumarização da descrição
/// do pull request e entrega da branch/commit/PR pela conta de agente autônomo.
/// </summary>
public sealed class LuckeOrchestratorWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<LuckeOrchestratorWorker> logger) : BackgroundService
{
    /// <summary>Prefixo da branch criada para a tarefa (ex.: <c>feat/task-{id}</c>).</summary>
    private const string BranchNamePrefix = "feat/task-";

    /// <summary>Documentos de referência que o RAG entrega ao expert por tarefa.</summary>
    private const int SimilarDocumentsLimit = 3;

    /// <summary>Instante (UTC) do último ciclo de indexação concluído — governa a cadência configurada.</summary>
    private DateTimeOffset _lastIndexingAtUtc = DateTimeOffset.MinValue;

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

            // Medidor de frustração da execução atual: cada falha (retorno vazio etc.) o aproxima do overdrive.
            var frustrationTracker = new FrustrationTracker(Math.Max(1, frustrationSettings.MaxFailures));

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

                var generatedCode = await provider
                    .GenerateCodeAsync(task.Payload, contextAnalysis, stoppingToken)
                    .ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(generatedCode))
                {
                    // Retorno vazio é falha para a mecânica de frustração: o circuito se aproxima do
                    // overdrive e a tarefa é encerrada como Falhou neste ciclo.
                    var overdriveTriggered = frustrationTracker.RegistrarFalha();

                    logger.LogWarning(
                        "Expert '{ModelName}' devolveu retorno vazio para a tarefa {TaskId}: falha {Falhas}/{Limite} (overdrive: {Overdrive}).",
                        provider.ModelName,
                        task.Id,
                        frustrationTracker.ContadorAtual,
                        frustrationTracker.LimiteMaximo,
                        overdriveTriggered);

                    await UpdateStatusAsync(taskRepository, task, AgentTaskStatus.Falhou, stoppingToken)
                        .ConfigureAwait(false);

                    continue;
                }

                // Braços (GitHub): branch de trabalho, commit com o artefato gerado e pull request.
                var branchName = $"{BranchNamePrefix}{task.Id}";

                await gitHubService.CreateBranchAsync(branchName, stoppingToken).ConfigureAwait(false);

                var artifacts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [options.GeneratedArtifactPath] = generatedCode
                };

                await gitHubService
                    .CommitChangesAsync(
                        branchName,
                        $"feat(task-{task.Id}): entrega automática do orquestrador",
                        artifacts,
                        stoppingToken)
                    .ConfigureAwait(false);

                // Descrição do PR: sumarizada por um expert rápido a partir da tarefa e do código.
                var pullRequestDescription = await BuildPullRequestSummaryAsync(
                        taskRouter,
                        task,
                        generatedCode,
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
                    "Tarefa {TaskId} concluída na branch '{Branch}'. Pull request: {PullRequestUrl}.",
                    task.Id,
                    branchName,
                    pullRequestUrl);
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
    /// seguinte. Sem resumo, o corpo do PR recebe a descrição determinística.
    /// </remarks>
    private async Task<string> BuildPullRequestSummaryAsync(
        ITaskRouter taskRouter,
        AgentTask task,
        string generatedCode,
        CancellationToken stoppingToken)
    {
        try
        {
            var summaryProvider = taskRouter.ResolveProvider(TaskComplexity.Baixo);

            var summary = await summaryProvider
                .GenerateCodeAsync(BuildPullRequestSummaryPrompt(task, generatedCode), string.Empty, stoppingToken)
                .ConfigureAwait(false);

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

    /// <summary>Prompt de sumarização enviado ao expert rápido para o corpo do pull request.</summary>
    private static string BuildPullRequestSummaryPrompt(AgentTask task, string generatedCode)
        => $"Crie um resumo curto em texto puro para a descrição de um Pull Request que implementou esta tarefa: {task.Payload}. "
            + $"O código gerado foi: {generatedCode}";
}
