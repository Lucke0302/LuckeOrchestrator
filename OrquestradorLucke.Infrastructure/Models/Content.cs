using System.Text.Json.Serialization;

namespace OrquestradorLucke.Infrastructure.Models;

/// <summary>Conteúdo de um candidato: papel do emissor e fragmentos de texto.</summary>
internal sealed record Content
{
    /// <summary>Papel do emissor (ex.: <c>model</c>).</summary>
    [JsonPropertyName("role")]
    public string? Role { get; init; }

    /// <summary>Fragmentos que compõem a resposta; podem incluir raciocínio e payload.</summary>
    [JsonPropertyName("parts")]
    public IReadOnlyList<Part>? Parts { get; init; }
}
