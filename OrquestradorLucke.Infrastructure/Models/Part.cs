using System.Text.Json.Serialization;

namespace OrquestradorLucke.Infrastructure.Models;

/// <summary>Fragmento de conteúdo da resposta do Google AI Studio.</summary>
internal sealed record Part
{
    /// <summary>Texto do fragmento — pode conter Chain-of-Thought antes do payload útil.</summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    /// <summary>
    /// Marcado pelo Google quando o fragmento é resumo de raciocínio (<c>thinking</c>) e não a
    /// resposta final; fragmentos assim são descartados na leitura.
    /// </summary>
    [JsonPropertyName("thought")]
    public bool? Thought { get; init; }
}
