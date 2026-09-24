using System.Text.Json.Serialization;

namespace OrquestradorLucke.Infrastructure.Models;

/// <summary>Candidato de resposta do Google AI Studio (item de <c>candidates</c>).</summary>
internal sealed record Candidate
{
    /// <summary>Conteúdo produzido pelo modelo.</summary>
    [JsonPropertyName("content")]
    public Content? Content { get; init; }

    /// <summary>Motivo do encerramento da geração (ex.: <c>STOP</c>, <c>MAX_TOKENS</c>, <c>SAFETY</c>).</summary>
    [JsonPropertyName("finishReason")]
    public string? FinishReason { get; init; }
}
