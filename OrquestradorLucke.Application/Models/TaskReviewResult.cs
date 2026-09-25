using OrquestradorLucke.Domain;

namespace OrquestradorLucke.Application.Models;

/// <summary>Desfecho de uma operação de revisão (aceitar/rejeitar) pedida pela API.</summary>
public enum TaskReviewOutcome
{
    /// <summary>Merge do pull request feito com o token administrativo; tarefa em <see cref="AgentTaskStatus.Aprovada"/>.</summary>
    Aprovada = 0,

    /// <summary>Pull request fechado com o motivo informado; tarefa devolvida para a fila.</summary>
    Rejeitada = 1,

    /// <summary>O identificador informado não corresponde a nenhuma tarefa.</summary>
    NaoEncontrada = 2,

    /// <summary>A tarefa não tem entrega no GitHub (sem branch/pull request) para o revisor agir.</summary>
    SemPullRequest = 3
}

/// <summary>
/// Resultado de um caso de uso de revisão: o desfecho, a tarefa (quando existir) e o detalhe que
/// explica o desfecho. O host traduz isso em status HTTP — a Application não conhece HTTP.
/// </summary>
/// <param name="Outcome">Desfecho da operação.</param>
/// <param name="Task">Tarefa resultante (ou a encontrada, quando a operação não pôde ser aplicada).</param>
/// <param name="Detail">Explicação legível do desfecho, publicada no erro da API quando houver falha.</param>
public sealed record TaskReviewResult(TaskReviewOutcome Outcome, AgentTask? Task, string? Detail)
{
    /// <summary>Indica que a revisão foi aplicada (aprovada ou rejeitada).</summary>
    public bool Succeeded => Outcome is TaskReviewOutcome.Aprovada or TaskReviewOutcome.Rejeitada;

    /// <summary>Aprovação concluída: PR mesclado e tarefa em <see cref="AgentTaskStatus.Aprovada"/>.</summary>
    public static TaskReviewResult Aprovada(AgentTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        return new TaskReviewResult(TaskReviewOutcome.Aprovada, task, null);
    }

    /// <summary>Rejeição concluída: PR fechado (quando havia) e tarefa de volta em <see cref="AgentTaskStatus.Pendente"/>.</summary>
    public static TaskReviewResult Rejeitada(AgentTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        return new TaskReviewResult(TaskReviewOutcome.Rejeitada, task, null);
    }

    /// <summary>Identificador sem tarefa correspondente.</summary>
    public static TaskReviewResult NaoEncontrada(Guid taskId)
        => new(TaskReviewOutcome.NaoEncontrada, null, $"Tarefa {taskId} não encontrada.");

    /// <summary>Tarefa sem pull request/branch: não há o que mesclar ou fechar.</summary>
    public static TaskReviewResult SemPullRequest(AgentTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        return new TaskReviewResult(
            TaskReviewOutcome.SemPullRequest,
            task,
            $"A tarefa {task.Id} não tem pull request de entrega para revisar.");
    }
}
