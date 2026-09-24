namespace OrquestradorLucke.Infrastructure.Configuration;

/// <summary>
/// Opções de acesso ao Google AI Studio. Valores sensíveis (ApiKey) devem vir de
/// user-secrets ou variáveis de ambiente — nunca hardcoded no código.
/// </summary>
public sealed class AiStudioOptions
{
    public const string SectionName = "AiStudio";

    /// <summary>URL base do endpoint do Google AI Studio (usada como BaseAddress do Named Client).</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Chave de API do Google AI Studio (user-secrets ou variável de ambiente).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Versão da API de geração de conteúdo (segmento inicial do caminho do endpoint).</summary>
    public string ApiVersion { get; set; } = "v1beta";

    /// <summary>
    /// Identificador do modelo atendido por este adapter (ex.: <c>models/gemma-4-26b-a4b-it</c>).
    /// Atribuído na composição — um expert por modelo do <c>ModelCatalog</c> — e não pelo appsettings.
    /// </summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>Janela de bloqueio do modelo quando a cota diária é esgotada (HTTP 429), em horas.</summary>
    public int QuotaLockoutHours { get; set; } = 24;

    /// <summary>Timeout das requisições HTTP, em segundos.</summary>
    public int TimeoutSeconds { get; set; } = 100;

    /// <summary>
    /// Cria uma cópia destas opções apontando para <paramref name="modelName"/>. Usado na composição
    /// para instanciar um expert por modelo do catálogo MoE reaproveitando BaseUrl/ApiKey/timeouts.
    /// </summary>
    /// <param name="modelName">Identificador do modelo no Google AI Studio.</param>
    /// <exception cref="ArgumentException">Quando <paramref name="modelName"/> for nulo, vazio ou apenas espaços.</exception>
    public AiStudioOptions ForModel(string modelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        return new AiStudioOptions
        {
            BaseUrl = BaseUrl,
            ApiKey = ApiKey,
            ApiVersion = ApiVersion,
            ModelName = modelName,
            QuotaLockoutHours = QuotaLockoutHours,
            TimeoutSeconds = TimeoutSeconds
        };
    }
}
