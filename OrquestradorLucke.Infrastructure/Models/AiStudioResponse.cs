using System.Text.Json.Serialization;

namespace OrquestradorLucke.Infrastructure.Models;

/// <summary>
/// Envelope de resposta do endpoint <c>models/{model}:generateContent</c> do Google AI Studio.
/// Mapeamento interno da camada de infraestrutura — não vaza para as camadas superiores.
/// </summary>
internal sealed record AiStudioResponse
{
    /// <summary>Respostas geradas. O orquestrador consome o primeiro candidato com texto útil.</summary>
    [JsonPropertyName("candidates")]
    public IReadOnlyList<Candidate>? Candidates { get; init; }
}
