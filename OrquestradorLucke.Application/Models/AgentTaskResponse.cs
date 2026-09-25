using OrquestradorLucke.Domain;

namespace OrquestradorLucke.Application.Models;

/// <summary>
/// Contrato de resposta da API de gerenciamento para uma <see cref="AgentTask"/>. O Domain não é
/// exposto cru ao cliente web: o DTO fixa os campos publicados (e o formato deles) para que uma
/// mudança interna do record não vire quebra de contrato no front-end.
/// </summary>
public sealed record AgentTaskResponse(
    Guid Id,
    string Payload,
    TaskComplexity Complexidade,
    AgentTaskStatus Status,
    DateTimeOffset CriadoEm,
    DateTimeOffset? AtualizadoEm,
    string? Branch,
    string? PullRequestUrl)
{
    /// <summary>Projeta a entidade do Domain no contrato HTTP.</summary>
    /// <param name="task">Tarefa a publicar.</param>
    public static AgentTaskResponse From(AgentTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        return new AgentTaskResponse(
            task.Id,
            task.Payload,
            task.Complexidade,
            task.Status,
            task.CriadoEm,
            task.AtualizadoEm,
            task.Branch,
            task.PullRequestUrl);
    }
}
