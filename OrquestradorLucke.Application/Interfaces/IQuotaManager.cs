namespace OrquestradorLucke.Application.Interfaces;

/// <summary>
/// Circuit Breaker de cota por modelo. Quando o provedor responde 429 (Too Many Requests), o
/// modelo é retirado do rodízio MoE pelo tempo da janela de bloqueio e o roteador passa a usar
/// o próximo modelo da cadeia de fallback.
/// </summary>
public interface IQuotaManager
{
    /// <summary>Informa se o modelo pode receber requisições neste momento.</summary>
    /// <param name="modelName">Identificador do modelo (ex.: <c>models/gemma-4-26b-a4b-it</c>).</param>
    /// <returns><c>true</c> quando o modelo está ativo (nunca bloqueado ou com janela já expirada).</returns>
    /// <exception cref="ArgumentException">Quando <paramref name="modelName"/> for nulo, vazio ou apenas espaços.</exception>
    bool IsModelAvailable(string modelName);

    /// <summary>
    /// Marca o modelo como indisponível pelo tempo informado (renovando a janela quando já existir
    /// um bloqueio mais longo em vigor).
    /// </summary>
    /// <param name="modelName">Identificador do modelo.</param>
    /// <param name="lockoutDuration">Duração do bloqueio (ex.: 24 horas para cota diária esgotada).</param>
    /// <exception cref="ArgumentException">Quando <paramref name="modelName"/> for nulo, vazio ou apenas espaços.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Quando <paramref name="lockoutDuration"/> não for maior que zero.</exception>
    void LockOutModel(string modelName, TimeSpan lockoutDuration);
}
