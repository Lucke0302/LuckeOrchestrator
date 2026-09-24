namespace OrquestradorLucke.Domain;

/// <summary>
/// Tarefa recebida pelo orquestrador: contém o conteúdo bruto a ser processado,
/// a complexidade classificada (base do roteamento MoE) e o status atual.
/// </summary>
public record AgentTask
{
    /// <summary>Identificador único da tarefa.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Conteúdo bruto recebido (ex.: issue, comentário ou instrução do usuário).</summary>
    public required string Payload { get; init; }

    /// <summary>Complexidade classificada, usada pelo roteador MoE.</summary>
    public TaskComplexity Complexidade { get; init; } = TaskComplexity.Baixo;

    /// <summary>Status atual da tarefa.</summary>
    public AgentTaskStatus Status { get; init; } = AgentTaskStatus.Pendente;

    /// <summary>Momento de criação da tarefa (UTC).</summary>
    public DateTimeOffset CriadoEm { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Momento da última atualização de status (UTC), quando houver.</summary>
    public DateTimeOffset? AtualizadoEm { get; init; }

    /// <summary>Branch criada pelo agente autônomo para entregar as alterações.</summary>
    public string? Branch { get; init; }

    /// <summary>URL do pull request aberto pelo agente autônomo.</summary>
    public string? PullRequestUrl { get; init; }
}
