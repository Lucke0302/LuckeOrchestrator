using OrquestradorLucke.Domain;

namespace OrquestradorLucke.Infrastructure.Configuration;

/// <summary>
/// Catálogo de modelos do padrão MoE: identificadores do Google AI Studio e as cadeias de
/// prioridade por complexidade usadas pelo fallback do Circuit Breaker de cota. É a única fonte
/// de verdade dos modelos — a composição (DI) registra um expert para cada item de
/// <see cref="AllModels"/>.
/// </summary>
/// <remarks>
/// Identificadores de modelo não são segredos nem URLs: BaseUrl, chave de API e timeouts continuam
/// vindo de configuração (IOptions/appsettings/user-secrets).
/// </remarks>
public static class ModelCatalog
{
    /// <summary>Gemma 4 26B (A4B) — expert de entrada da complexidade baixa.</summary>
    public const string Gemma4_26bA4bIt = "models/gemma-4-26b-a4b-it";

    /// <summary>Gemma 4 31B — expert de entrada da complexidade média.</summary>
    public const string Gemma4_31bIt = "models/gemma-4-31b-it";

    /// <summary>Gemini 3.5 Flash Lite — fallback da complexidade baixa.</summary>
    public const string Gemini35FlashLite = "models/gemini-3.5-flash-lite";

    /// <summary>Gemini 3.1 Flash Lite — fallback da complexidade média.</summary>
    public const string Gemini31FlashLite = "models/gemini-3.1-flash-lite";

    /// <summary>Gemini 3.5 Flash — expert de entrada da complexidade alta.</summary>
    public const string Gemini35Flash = "models/gemini-3.5-flash";

    /// <summary>Gemini 3.6 Flash — segundo nível da complexidade alta e entrada da crítica.</summary>
    public const string Gemini36Flash = "models/gemini-3.6-flash";

    /// <summary>Gemini 3.7 Flash — terceiro nível da complexidade alta.</summary>
    public const string Gemini37Flash = "models/gemini-3.7-flash";

    /// <summary>Gemini 3.8 Flash — fallback da complexidade crítica.</summary>
    public const string Gemini38Flash = "models/gemini-3.8-flash";

    /// <summary>Gemini 3 Flash Preview — último elo da cadeia crítica.</summary>
    public const string Gemini3FlashPreview = "models/gemini-3-flash-preview";

    /// <summary>
    /// Modelo de embeddings do RAG (base de código). Não participa das cadeias MoE: não gera código,
    /// apenas vetores que alimentam a coluna <c>vector</c> de <c>code_documents</c>.
    /// </summary>
    public const string TextEmbedding004 = "models/text-embedding-004";

    /// <summary>
    /// Dimensão dos vetores produzidos por <see cref="TextEmbedding004"/> — é o <c>d</c> da coluna
    /// <c>vector(d)</c> do pgvector e precisa ser fixo para o índice e as consultas casarem.
    /// </summary>
    public const int EmbeddingDimensions = 768;

    /// <summary>
    /// Cadeias de prioridade por complexidade; a ordem define o fallback dentro da complexidade
    /// (primeiro modelo com cota ativa vence).
    /// </summary>
    internal static readonly IReadOnlyDictionary<TaskComplexity, IReadOnlyList<string>> Chains =
        new Dictionary<TaskComplexity, IReadOnlyList<string>>
        {
            [TaskComplexity.Baixo] = [Gemma4_26bA4bIt, Gemini35FlashLite],
            [TaskComplexity.Medio] = [Gemma4_31bIt, Gemini31FlashLite],
            [TaskComplexity.Alto] = [Gemini35Flash, Gemini36Flash, Gemini37Flash],
            [TaskComplexity.Critico] = [Gemini36Flash, Gemini38Flash, Gemini3FlashPreview]
        };

    /// <summary>Todos os modelos distintos mapeados nas cadeias (um expert por modelo na DI).</summary>
    public static IReadOnlyList<string> AllModels { get; } =
        [.. Chains.Values.SelectMany(chain => chain).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Devolve a cadeia de prioridade da complexidade informada.</summary>
    /// <param name="complexity">Complexidade classificada da tarefa.</param>
    /// <exception cref="InvalidOperationException">Quando a complexidade não possui cadeia mapeada.</exception>
    public static IReadOnlyList<string> GetChain(TaskComplexity complexity)
        => Chains.TryGetValue(complexity, out var chain)
            ? chain
            : throw new InvalidOperationException($"Complexidade '{complexity}' não possui cadeia de modelos mapeada em {nameof(ModelCatalog)}.");
}
