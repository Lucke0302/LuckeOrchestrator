namespace OrquestradorLucke.Domain;

/// <summary>
/// Estado do Circuit Breaker de cota de um provedor: até quando o modelo está retirado do rodízio
/// MoE. É a versão persistida do bloqueio aplicado no HTTP 429 — sobrevive ao reinício do daemon e
/// é compartilhada por todas as instâncias que apontam para o mesmo banco.
/// </summary>
public record QuotaState
{
    /// <summary>Identificador único do registro.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Identificador do modelo/provedor no catálogo (ex.: <c>models/gemma-4-26b-a4b-it</c>) —
    /// chave natural do bloqueio.
    /// </summary>
    public required string ProviderName { get; init; }

    /// <summary>Instante (UTC) até o qual o provedor permanece bloqueado.</summary>
    public DateTimeOffset LockedUntil { get; init; }
}
