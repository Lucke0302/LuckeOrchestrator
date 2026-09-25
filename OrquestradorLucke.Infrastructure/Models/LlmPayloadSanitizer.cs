namespace OrquestradorLucke.Infrastructure.Models;

/// <summary>
/// Limpeza defensiva do payload devolvido pelo LLM, exposta como API pública única: a regra de
/// sanitização (<see cref="AiStudioResponseReader.SanitizeJsonPayload"/>, interna ao adapter do AI
/// Studio) é a MESMA usada no parse dos artefatos gerados e no parse do resumo do pull request no
/// host — duas implementações divergentes seriam duas causas possíveis de fallback silencioso.
/// </summary>
public static class LlmPayloadSanitizer
{
    /// <summary>
    /// Remove cercas de markdown, o rótulo <c>json</c> e resíduos de prosa da resposta do modelo,
    /// devolvendo o texto pronto para <c>JsonSerializer.Deserialize</c>.
    /// </summary>
    /// <param name="modelOutput">Resposta do modelo (bruta ou já sem o raciocínio).</param>
    /// <returns>Payload sanitizado; string vazia quando não há texto.</returns>
    public static string SanitizeJson(string? modelOutput)
        => AiStudioResponseReader.SanitizeJsonPayload(modelOutput ?? string.Empty);
}
