using Microsoft.Extensions.Options;
using OrquestradorLucke.Application.Configuration;
using OrquestradorLucke.Application.Interfaces;
using OrquestradorLucke.Domain;
using OrquestradorLucke.Worker.Configuration;

namespace OrquestradorLucke.Worker;

/// <summary>
/// Laço principal do orquestrador. Cada iteração abre um escopo próprio de DI — para que nenhuma
/// dependência (DbContext, adapters, Typed Clients) carregue estado entre execuções — e percorre o
/// ciclo completo: dequeue da fila, roteamento MoE da complexidade, geração do artefato pelo expert
/// e entrega da branch/commit/pull request pela conta de agente autônomo.
/// </summary>
public sealed class LuckeOrchestratorWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<LuckeOrchestratorWorker> logger) : BackgroundService
{
    /// <summary>Prefixo da branch criada para a tarefa (ex.: <c>feat/task-{id}</c>).</summary>
    private const string BranchNamePrefix = "feat/task-";

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

            var options = services.GetRequiredService<IOptions<OrchestratorWorkerOptions>>().Value;
            var frustrationSettings = services.GetRequiredService<IOptions<FrustrationSettings>>().Value;

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

                // Contexto inicial vazio: a análise prévia consumiria cota antes da geração e não é
                // pré-requisito do artefato entregue neste ciclo.
                var generatedCode = await provider
                    .GenerateCodeAsync(task.Payload, string.Empty, stoppingToken)
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

                var pullRequestUrl = await gitHubService
                    .OpenPullRequestAsync(
                        branchName,
                        $"feat(task-{task.Id})",
                        BuildPullRequestDescription(task),
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

    /// <summary>Corpo do pull request: identifica a tarefa de origem e reproduz o payload recebido.</summary>
    private static string BuildPullRequestDescription(AgentTask task)
        => $"Entrega automática da tarefa `{task.Id}` pelo orquestrador Lucke."
            + $"{Environment.NewLine}{Environment.NewLine}**Payload original**"
            + $"{Environment.NewLine}{task.Payload}";
}
