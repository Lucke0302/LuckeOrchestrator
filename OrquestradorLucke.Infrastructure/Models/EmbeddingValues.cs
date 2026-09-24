using System.Text.Json.Serialization;

namespace OrquestradorLucke.Infrastructure.Models;

/// <summary>
/// Vetor produzido pelo modelo de embeddings (<c>embedding.values</c> da resposta do
/// <c>embedContent</c>).
/// </summary>
internal sealed record EmbeddingValues
{
    /// <summary>Valores do vetor, na ordem em que o modelo os devolveu.</summary>
    [JsonPropertyName("values")]
    public IReadOnlyList<float>? Values { get; init; }
}
