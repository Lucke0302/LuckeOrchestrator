namespace OrquestradorLucke.Domain;

/// <summary>
/// Sinaliza que a cota do modelo foi esgotada (HTTP 429 do Google AI Studio) e que ele foi
/// retirado temporariamente do rodízio MoE pelo Circuit Breaker de cota.
/// </summary>
/// <remarks>
/// Deriva de <see cref="Exception"/> — e não de <see cref="HttpRequestException"/> — de propósito:
/// políticas de resiliência tratam <see cref="HttpRequestException"/> como falha transitória e
/// repetiriam a chamada. Cota esgotada não se recupera com retry: a exceção deve atravessar a
/// política sem reexecução e ser resolvida pelo fallback do roteador (próximo modelo da cadeia).
/// </remarks>
public sealed class QuotaExhaustedException : Exception
{
    /// <summary>
    /// Modelo cuja cota foi esgotada. <c>null</c> quando a falha não é de um modelo específico
    /// (ex.: todas as cadeias de complexidade bloqueadas).
    /// </summary>
    public string? ModelName { get; }

    /// <summary>Momento (UTC) em que o modelo volta a estar disponível, quando conhecido.</summary>
    public DateTimeOffset? AvailableAtUtc { get; }

    public QuotaExhaustedException()
    {
    }

    public QuotaExhaustedException(string message)
        : base(message)
    {
    }

    public QuotaExhaustedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <param name="message">Descrição do bloqueio de cota.</param>
    /// <param name="modelName">Modelo cuja cota foi esgotada.</param>
    /// <param name="availableAtUtc">Momento (UTC) em que o modelo volta a estar disponível.</param>
    public QuotaExhaustedException(string message, string modelName, DateTimeOffset availableAtUtc)
        : base(message)
    {
        ModelName = modelName;
        AvailableAtUtc = availableAtUtc;
    }

    /// <param name="message">Descrição do bloqueio de cota.</param>
    /// <param name="modelName">Modelo cuja cota foi esgotada.</param>
    /// <param name="availableAtUtc">Momento (UTC) em que o modelo volta a estar disponível.</param>
    /// <param name="innerException">Causa original da falha.</param>
    public QuotaExhaustedException(string message, string modelName, DateTimeOffset availableAtUtc, Exception innerException)
        : base(message, innerException)
    {
        ModelName = modelName;
        AvailableAtUtc = availableAtUtc;
    }
}
