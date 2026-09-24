namespace OrquestradorLucke.Infrastructure.Configuration;

/// <summary>
/// Parâmetros da política de resiliência aplicada aos Named Clients do Google AI Studio
/// (retry com backoff exponencial para falhas transitórias: 5xx, 408 e HttpRequestException).
/// Lidos da seção <c>HttpResilience</c> — sem valores sensíveis e sem hardcoding.
/// </summary>
public sealed class HttpResilienceOptions
{
    public const string SectionName = "HttpResilience";

    /// <summary>Quantidade de tentativas adicionais após a primeira falha transitória.</summary>
    public int RetryAttempts { get; set; } = 3;

    /// <summary>Base do backoff exponencial em segundos (tentativa N aguarda BaseDelay * 2^(N-1)).</summary>
    public int BaseDelaySeconds { get; set; } = 2;
}
