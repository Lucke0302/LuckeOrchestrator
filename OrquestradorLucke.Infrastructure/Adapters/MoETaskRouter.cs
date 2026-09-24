using OrquestradorLucke.Application.Interfaces;
using OrquestradorLucke.Domain;
using OrquestradorLucke.Infrastructure.Configuration;

namespace OrquestradorLucke.Infrastructure.Adapters;

/// <summary>
/// Roteador dinâmico (MoE) com cadeia de fallback e Circuit Breaker de cota. Cada complexidade
/// possui uma lista de prioridade definida no <see cref="ModelCatalog"/>; o roteador devolve o
/// primeiro modelo com cota ativa. Se a cadeia inteira estiver bloqueada, a tarefa "sobe" para a
/// cadeia da complexidade superior (mais robusta).
/// </summary>
public sealed class MoETaskRouter : ITaskRouter
{
    private readonly IReadOnlyDictionary<string, ILLMProvider> _providers;
    private readonly IQuotaManager _quotaManager;

    /// <param name="providers">Experts registrados na DI (um por modelo do catálogo MoE).</param>
    /// <param name="quotaManager">Circuit Breaker de cota que informa quais modelos estão ativos.</param>
    /// <exception cref="InvalidOperationException">
    /// Quando nenhum expert está registrado, há modelos duplicados/sem identificação ou o catálogo
    /// referencia modelos sem expert registrado.
    /// </exception>
    public MoETaskRouter(IEnumerable<ILLMProvider> providers, IQuotaManager quotaManager)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(quotaManager);

        _quotaManager = quotaManager;
        _providers = BuildProviderIndex(providers);

        EnsureCatalogIsServable();
    }

    /// <inheritdoc />
    public ILLMProvider ResolveProvider(TaskComplexity complexity)
    {
        for (var level = (int)complexity; level <= (int)TaskComplexity.Critico; level++)
        {
            foreach (var modelName in ModelCatalog.GetChain((TaskComplexity)level))
            {
                if (!_quotaManager.IsModelAvailable(modelName))
                {
                    continue;
                }

                if (_providers.TryGetValue(modelName, out var provider))
                {
                    return provider;
                }
            }

            // Cadeia inteira bloqueada pela cota: a tarefa sobe para a complexidade superior.
        }

        throw new QuotaExhaustedException(
            $"Nenhum expert disponível: as cadeias de {complexity} até {TaskComplexity.Critico} estão bloqueadas pelo Circuit Breaker de cota.");
    }

    /// <inheritdoc />
    /// <remarks>O overdrive usa a cadeia mais robusta (<see cref="TaskComplexity.Critico"/>), com o fallback interno dela.</remarks>
    public ILLMProvider ResolveOverdriveProvider() => ResolveProvider(TaskComplexity.Critico);

    /// <summary>Indexa os experts pelo <see cref="ILLMProvider.ModelName"/> (comparação sem diferenciar maiúsculas).</summary>
    private static IReadOnlyDictionary<string, ILLMProvider> BuildProviderIndex(IEnumerable<ILLMProvider> providers)
    {
        var index = new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in providers)
        {
            if (string.IsNullOrWhiteSpace(provider.ModelName))
            {
                throw new InvalidOperationException($"O expert '{provider.GetType().Name}' não informou ModelName.");
            }

            if (!index.TryAdd(provider.ModelName, provider))
            {
                throw new InvalidOperationException($"Mais de um expert foi registrado para o modelo '{provider.ModelName}'.");
            }
        }

        if (index.Count == 0)
        {
            throw new InvalidOperationException("Nenhum expert (ILLMProvider) foi registrado na injeção de dependência.");
        }

        return index;
    }

    /// <summary>
    /// Falha rápido na composição quando o catálogo referencia um modelo sem expert registrado,
    /// em vez de descobrir o problema apenas durante o fallback em produção.
    /// </summary>
    private void EnsureCatalogIsServable()
    {
        var missing = ModelCatalog.Chains
            .SelectMany(chain => chain.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(modelName => !_providers.ContainsKey(modelName))
            .ToArray();

        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"Modelos do catálogo MoE sem expert registrado na injeção de dependência: {string.Join(", ", missing)}.");
        }
    }
}

