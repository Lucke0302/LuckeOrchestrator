using Microsoft.EntityFrameworkCore;
using OrquestradorLucke.Application.Interfaces;
using OrquestradorLucke.Domain;

namespace OrquestradorLucke.Infrastructure.Data.Repositories;

/// <summary>
/// Repositório de tarefas em PostgreSQL. O dequeue é feito em duas etapas: leitura da pendente mais
/// antiga sem rastreamento (a entidade é imutável) e UPDATE condicional no banco — se outra
/// instância do worker reivindicar a mesma tarefa no intervalo, nenhuma linha é afetada e a
/// reivindicação é descartada, evitando processamento duplicado.
/// </summary>
public sealed class AgentTaskRepository(AppDbContext context) : IAgentTaskRepository
{
    /// <inheritdoc />
    public async Task<AgentTask?> GetNextPendingTaskAsync(CancellationToken cancellationToken)
    {
        // AsNoTracking: a instância devolvida ao orquestrador é reconstruída por "with" e não deve
        // disputar o change tracker com o restante da iteração.
        var candidate = await context.AgentTasks
            .AsNoTracking()
            .Where(task => task.Status == AgentTaskStatus.Pendente)
            .OrderBy(task => task.CriadoEm)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (candidate is null)
        {
            return null;
        }

        var claimedTask = candidate with
        {
            Status = AgentTaskStatus.EmExecucao,
            AtualizadoEm = DateTimeOffset.UtcNow
        };

        var claimedRows = await context.AgentTasks
            .Where(task => task.Id == claimedTask.Id && task.Status == AgentTaskStatus.Pendente)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(task => task.Status, claimedTask.Status)
                    .SetProperty(task => task.AtualizadoEm, claimedTask.AtualizadoEm),
                cancellationToken)
            .ConfigureAwait(false);

        return claimedRows == 1 ? claimedTask : null;
    }

    /// <inheritdoc />
    public async Task UpdateTaskAsync(AgentTask task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);

        // A tarefa chega destacada do contexto (veio do dequeue), por isso Update anexa e grava
        // todas as colunas do record — as propriedades init-only são escritas pelos accessors do EF.
        context.AgentTasks.Update(task);

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AddTaskAsync(AgentTask task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);

        // O Id vem do domínio (Guid.NewGuid()) e nasce Pendente: o próximo ciclo do laço reivindica a
        // linha pelo mesmo dequeue que atende as tarefas inseridas direto no banco.
        context.AgentTasks.Add(task);

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AgentTask?> GetTaskByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        // Sem rastreamento: a tarefa é usada para decidir a revisão e a gravação posterior passa pelo
        // Update condicional do repositório (o change tracker não disputa a instância devolvida).
        return await context.AgentTasks
            .AsNoTracking()
            .FirstOrDefaultAsync(task => task.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentTask>> GetTasksAsync(int limit, CancellationToken cancellationToken)
    {
        var effectiveLimit = Math.Max(1, limit);

        // Mais recentes primeiro: é a ordem que o painel web exibe e a leitura do índice é pequena
        // (o teto de linhas vem do caso de uso, não desta consulta).
        return await context.AgentTasks
            .AsNoTracking()
            .OrderByDescending(task => task.CriadoEm)
            .Take(effectiveLimit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
