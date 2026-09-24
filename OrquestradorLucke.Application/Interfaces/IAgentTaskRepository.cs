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
}
