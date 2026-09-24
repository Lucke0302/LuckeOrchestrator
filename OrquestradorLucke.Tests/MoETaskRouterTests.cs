using FluentAssertions;
using Moq;
using OrquestradorLucke.Application.Interfaces;
using OrquestradorLucke.Domain;
using OrquestradorLucke.Infrastructure.Adapters;
using OrquestradorLucke.Infrastructure.Configuration;
using OrquestradorLucke.Infrastructure.Quota;

namespace OrquestradorLucke.Tests;

/// <summary>
/// Comportamento do roteador MoE (<see cref="MoETaskRouter"/>): a cadeia de prioridade do
/// <see cref="ModelCatalog"/> é percorrida na ordem da complexidade, o Circuit Breaker de cota
/// bloqueia modelo por modelo e o overdrive resolve a cadeia mais robusta.
/// </summary>
/// <remarks>
/// Os experts são mocks de <see cref="ILLMProvider"/> — o roteador só consulta <c>ModelName</c> — e o
/// Circuit Breaker de cota é o <see cref="InMemoryQuotaManager"/> real, para exercitar o bloqueio
/// como ele acontece em produção. As expectativas são derivadas do próprio catálogo
/// (<see cref="ModelCatalog.GetChain"/>), sem duplicar a ordem das cadeias no teste.
/// </remarks>
public sealed class MoETaskRouterTests
{
    [Theory]
    [InlineData(TaskComplexity.Baixo)]
    [InlineData(TaskComplexity.Medio)]
    [InlineData(TaskComplexity.Alto)]
    [InlineData(TaskComplexity.Critico)]
    public void ResolveProvider_DeveDevolverOCabecaDaCadeiaDaComplexidade(TaskComplexity complexidade)
    {
        var router = CreateRouter(new InMemoryQuotaManager());
        var expectedModel = ModelCatalog.GetChain(complexidade)[0];

        var provider = router.ResolveProvider(complexidade);

        provider.ModelName.Should().Be(expectedModel);
    }

    [Theory]
    [InlineData(TaskComplexity.Baixo)]
    [InlineData(TaskComplexity.Medio)]
    [InlineData(TaskComplexity.Alto)]
    [InlineData(TaskComplexity.Critico)]
    public void ResolveProvider_DevePercorrerACadeiaNaOrdemDePrioridade(TaskComplexity complexidade)
    {
        var quotaManager = new InMemoryQuotaManager();
        var router = CreateRouter(quotaManager);
        var chain = ModelCatalog.GetChain(complexidade);

        // Cada bloqueio de cota do modelo atual deve promover exatamente o próximo da cadeia.
        foreach (var expectedModel in chain)
        {
            router.ResolveProvider(complexidade).ModelName.Should().Be(expectedModel);

            quotaManager.LockOutModel(expectedModel, TimeSpan.FromHours(1));
        }
    }

    [Fact]
    public void ResolveProvider_ComCadeiaInteiraBloqueada_DeveSubirParaAComplexidadeSuperior()
    {
        var quotaManager = new InMemoryQuotaManager();
        var router = CreateRouter(quotaManager);

        var blockedModels = ModelCatalog
            .GetChain(TaskComplexity.Baixo)
            .Concat(ModelCatalog.GetChain(TaskComplexity.Medio));

        foreach (var model in blockedModels)
        {
            quotaManager.LockOutModel(model, TimeSpan.FromHours(1));
        }

        var expectedModel = ModelCatalog.GetChain(TaskComplexity.Alto)[0];

        var provider = router.ResolveProvider(TaskComplexity.Baixo);

        provider.ModelName.Should().Be(expectedModel);
    }

    [Fact]
    public void ResolveProvider_ComTodosOsModelosBloqueados_DeveLancarQuotaExhaustedException()
    {
        var quotaManager = new InMemoryQuotaManager();
        var router = CreateRouter(quotaManager);

        foreach (var model in ModelCatalog.AllModels)
        {
            quotaManager.LockOutModel(model, TimeSpan.FromHours(1));
        }

        var act = () => router.ResolveProvider(TaskComplexity.Baixo);

        act.Should().Throw<QuotaExhaustedException>();
    }

    [Fact]
    public void ResolveOverdriveProvider_DeveUsarACadeiaMaisRobusta()
    {
        var router = CreateRouter(new InMemoryQuotaManager());
        var expectedModel = ModelCatalog.GetChain(TaskComplexity.Critico)[0];

        var provider = router.ResolveOverdriveProvider();

        provider.ModelName.Should().Be(expectedModel);
    }

    [Fact]
    public void ResolveOverdriveProvider_ComOCabecaDaCadeiaCriticaBloqueado_DeveUsarOFallback()
    {
        var quotaManager = new InMemoryQuotaManager();
        var router = CreateRouter(quotaManager);
        var criticalChain = ModelCatalog.GetChain(TaskComplexity.Critico);

        foreach (var model in criticalChain.Take(criticalChain.Count - 1))
        {
            quotaManager.LockOutModel(model, TimeSpan.FromHours(1));
        }

        var provider = router.ResolveOverdriveProvider();

        provider.ModelName.Should().Be(criticalChain[^1]);
    }

    [Fact]
    public void Construtor_ComModeloDoCatalogoSemExpertRegistrado_DeveFalharRapido()
    {
        // Todos os experts, menos o cabeça da cadeia crítica: o roteador deve recusar a composição.
        var unservedModel = ModelCatalog.GetChain(TaskComplexity.Critico)[0];

        var providers = ModelCatalog.AllModels
            .Where(model => !string.Equals(model, unservedModel, StringComparison.OrdinalIgnoreCase))
            .Select(CreateProvider)
            .ToArray();

        var act = () => new MoETaskRouter(providers, new InMemoryQuotaManager());

        act.Should().Throw<InvalidOperationException>().WithMessage("*sem expert registrado*");
    }

    /// <summary>Cria o roteador com um expert (mock) para cada modelo do catálogo MoE.</summary>
    private static MoETaskRouter CreateRouter(IQuotaManager quotaManager)
        => new(ModelCatalog.AllModels.Select(CreateProvider).ToArray(), quotaManager);

    /// <summary>
    /// Expert de teste. O mock é estrito de propósito: o roteador só observa
    /// <see cref="ILLMProvider.ModelName"/>, portanto qualquer nova dependência do roteador em outra
    /// operação do provedor falha o teste em vez de passar silenciosamente.
    /// </summary>
    private static ILLMProvider CreateProvider(string modelName)
    {
        var provider = new Mock<ILLMProvider>(MockBehavior.Strict);
        provider.SetupGet(candidate => candidate.ModelName).Returns(modelName);

        return provider.Object;
    }
}
