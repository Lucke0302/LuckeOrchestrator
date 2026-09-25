using OrquestradorLucke.Domain;

namespace OrquestradorLucke.Application.Interfaces;

/// <summary>
/// Persistência das tarefas do orquestrador. A implementação real (PostgreSQL/EF Core) vive na
/// Infrastructure; a Application conhece apenas o contrato.
/// </summary>
public interface IAgentTaskRepository
{
    /// <summary>
    /// Reivindica a tarefa mais antiga com status <see cref="AgentTaskStatus.Pendente"/> e a marca
    /// como <see cref="AgentTaskStatus.EmExecucao"/>, atualizando o timestamp — o dequeue da fila.
    /// </summary>
    /// <param name="cancellationToken">Token de cancelamento da iteração do worker.</param>
    /// <returns>A tarefa reivindicada ou <c>null</c> quando não há pendentes.</returns>
    Task<AgentTask?> GetNextPendingTaskAsync(CancellationToken cancellationToken);

    /// <summary>Persiste o estado atual da tarefa (status, branch e URL do pull request).</summary>
    /// <param name="task">Tarefa com os valores finais a gravar.</param>
    /// <param name="cancellationToken">Token de cancelamento da iteração do worker.</param>
    Task UpdateTaskAsync(AgentTask task, CancellationToken cancellationToken);

    /// <summary>
    /// Enfileira uma tarefa nova (usada pela API de gerenciamento): a linha entra como
    /// <see cref="AgentTaskStatus.Pendente"/> e o laço do daemon a reivindica no próximo ciclo —
    /// sem reinício do host.
    /// </summary>
    /// <param name="task">Tarefa já com <c>Id</c> e status definidos pelo domínio.</param>
    /// <param name="cancellationToken">Token de cancelamento da requisição HTTP.</param>
    Task AddTaskAsync(AgentTask task, CancellationToken cancellationToken);

    /// <summary>Busca uma tarefa pelo identificador.</summary>
    /// <param name="id">Identificador da tarefa.</param>
    /// <param name="cancellationToken">Token de cancelamento da requisição HTTP.</param>
    /// <returns>A tarefa encontrada ou <c>null</c> quando o identificador não existe.</returns>
    Task<AgentTask?> GetTaskByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Lista as tarefas mais recentes para o painel web, da mais nova para a mais antiga.
    /// </summary>
    /// <param name="limit">Quantidade máxima de tarefas devolvidas.</param>
    /// <param name="cancellationToken">Token de cancelamento da requisição HTTP.</param>
    Task<IReadOnlyList<AgentTask>> GetTasksAsync(int limit, CancellationToken cancellationToken);
}
