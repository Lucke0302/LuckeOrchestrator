using System.Text.Json.Serialization;

namespace OrquestradorLucke.Infrastructure.Models;

/// <summary>
/// Envelope de resposta do endpoint <c>models/{model}:embedContent</c> do Google AI Studio.
/// Mapeamento interno da camada de infraestrutura — não vaza para as camadas superiores.
/// </summary>
internal sealed record EmbeddingResponse
{
    /// <summary>Vetor produzido pelo modelo de embeddings.</summary>
    [JsonPropertyName("embedding")]
    public EmbeddingValues? Embedding { get; init; }
}
