using OrquestradorLucke.Application.Interfaces;
using OrquestradorLucke.Application.Models;
using OrquestradorLucke.Domain;

namespace OrquestradorLucke.Application.Services;

/// <summary>
/// Casos de uso da API de gerenciamento/revisão: enfileirar tarefas, listá-las e aplicar a decisão do
/// revisor humano (<c>accept</c>/<c>reject</c>) sobre a entrega feita no GitHub.
/// </summary>
/// <remarks>
/// Serviço Scoped: consome o repositório de tarefas e o <see cref="IGitHubService"/> (que carregam o
/// <c>DbContext</c> e os clientes HTTP do escopo) e por isso é resolvido dentro do escopo da
/// requisição HTTP — nunca injetado em um Singleton.
/// <para>
/// A revisão é a única porta que <b>mescla</b> ou <b>fecha</b> pull request: essas operações usam a
/// identidade administrativa (<c>GitHub:AdminToken</c>), enquanto a entrega do agente usa
/// <c>GitHub:AgentToken</c>. O adapter decide o token pelo contexto da ação, não o caso de uso.
/// </para>
/// </remarks>
public sealed class TaskReviewService(
    IAgentTaskRepository taskRepository,
    IGitHubService gitHubService,
    FrustrationTracker frustrationTracker)
{
    /// <summary>Quantidade de tarefas devolvida pela listagem quando o cliente não informa <c>limit</c>.</summary>
    public const int DefaultTaskListLimit = 50;

    /// <summary>
    /// Teto da listagem: sem ele, um repositório antigo devolveria a tabela inteira (com payloads
    /// completos) em uma única resposta HTTP.
    /// </summary>
    public const int MaxTaskListLimit = 200;

    /// <summary>Lista as tarefas mais recentes para o painel web, da mais nova para a mais antiga.</summary>
    /// <param name="limit">Quantidade pedida pelo cliente; nula usa <see cref="DefaultTaskListLimit"/>.</param>
    /// <param name="cancellationToken">Token de cancelamento da requisição HTTP.</param>
    public async Task<IReadOnlyList<AgentTask>> GetTasksAsync(int? limit, CancellationToken cancellationToken)
    {
        var effectiveLimit = Math.Clamp(limit ?? DefaultTaskListLimit, 1, MaxTaskListLimit);

        return await taskRepository
            .GetTasksAsync(effectiveLimit, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Enfileira uma tarefa nova. O <c>Id</c> (UUID) e o status <see cref="AgentTaskStatus.Pendente"/>
    /// nascem no Domain — a API não os inventa.
    /// </summary>
    /// <param name="payload">Conteúdo bruto da tarefa.</param>
    /// <param name="complexidade">Complexidade que o roteador MoE usa para escolher o expert.</param>
    /// <param name="cancellationToken">Token de cancelamento da requisição HTTP.</param>
    /// <returns>A tarefa persistida, pronta para ser publicada na resposta.</returns>
    public async Task<AgentTask> CreateTaskAsync(
        string payload,
        TaskComplexity complexidade,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        var task = new AgentTask
        {
            Payload = payload,
            Complexidade = complexidade
        };

        await taskRepository.AddTaskAsync(task, cancellationToken).ConfigureAwait(false);

        return task;
    }

    /// <summary>
    /// Aprova a entrega: mescla o pull request da tarefa com o token administrativo e a marca como
    /// <see cref="AgentTaskStatus.Aprovada"/>.
    /// </summary>
    /// <remarks>
    /// A ordem é intencional: primeiro o efeito no GitHub, depois a persistência. Se o merge falhar,
    /// nenhuma exceção é engolida e o status permanece o anterior — o revisor pode tentar de novo com
    /// a tarefa ainda em <see cref="AgentTaskStatus.Concluida"/>.
    /// </remarks>
    /// <param name="taskId">Identificador da tarefa revisada.</param>
    /// <param name="cancellationToken">Token de cancelamento da requisição HTTP.</param>
    public async Task<TaskReviewResult> AcceptAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var task = await taskRepository
            .GetTaskByIdAsync(taskId, cancellationToken)
            .ConfigureAwait(false);

        if (task is null)
        {
            return TaskReviewResult.NaoEncontrada(taskId);
        }

        if (string.IsNullOrWhiteSpace(task.Branch))
        {
            // Sem branch não existe pull request a mesclar (ex.: tarefa que falhou antes da entrega).
            return TaskReviewResult.SemPullRequest(task);
        }

        await gitHubService
            .MergePullRequestAsync(
                task.Branch,
                $"Merge da tarefa {task.Id} (revisão aprovada)",
                cancellationToken)
            .ConfigureAwait(false);

        var approved = task with
        {
            Status = AgentTaskStatus.Aprovada,
            AtualizadoEm = DateTimeOffset.UtcNow
        };

        await taskRepository.UpdateTaskAsync(approved, cancellationToken).ConfigureAwait(false);

        return TaskReviewResult.Aprovada(approved);
    }

    /// <summary>
    /// Rejeita a entrega: fecha o pull request (com o motivo como comentário), registra a falha no
    /// medidor de frustração do daemon e devolve a tarefa para
    /// <see cref="AgentTaskStatus.Pendente"/> — o laço a reprocessa no próximo ciclo.
    /// </summary>
    /// <remarks>
    /// A falha entra no <see cref="FrustrationTracker"/> compartilhado com o laço do orquestrador:
    /// rejeições acumuladas aproximam o circuito do overdrive, então o próximo ciclo tende a escalar
    /// para o modelo mais robusto em vez de repetir o erro apontado pelo revisor.
    /// <para>
    /// A <c>Branch</c> e a <c>PullRequestUrl</c> são preservadas de propósito: elas dizem qual entrega
    /// foi rejeitada, e a branch é reaproveitada (nome determinístico <c>feat/task-{id}</c>) quando a
    /// tarefa for reentregue.
    /// </para>
    /// </remarks>
    /// <param name="taskId">Identificador da tarefa revisada.</param>
    /// <param name="motivo">Justificativa da rejeição informada pelo revisor.</param>
    /// <param name="cancellationToken">Token de cancelamento da requisição HTTP.</param>
    public async Task<TaskReviewResult> RejectAsync(Guid taskId, string motivo, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(motivo);

        var task = await taskRepository
            .GetTaskByIdAsync(taskId, cancellationToken)
            .ConfigureAwait(false);

        if (task is null)
        {
            return TaskReviewResult.NaoEncontrada(taskId);
        }

        // Fechamento idempotente: tarefa sem branch (falhou antes da entrega) não tem PR aberto para
        // fechar — a rejeição do trabalho continua valendo e a tarefa volta para a fila do mesmo jeito.
        if (!string.IsNullOrWhiteSpace(task.Branch))
        {
            await gitHubService
                .ClosePullRequestAsync(task.Branch, motivo, cancellationToken)
                .ConfigureAwait(false);
        }

        frustrationTracker.RegistrarFalha($"revisão humana rejeitou a entrega: {motivo}");

        var returned = task with
        {
            Status = AgentTaskStatus.Pendente,
            AtualizadoEm = DateTimeOffset.UtcNow
        };

        await taskRepository.UpdateTaskAsync(returned, cancellationToken).ConfigureAwait(false);

        return TaskReviewResult.Rejeitada(returned);
    }
}
