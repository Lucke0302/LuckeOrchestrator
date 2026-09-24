using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrquestradorLucke.Application.Interfaces;
using OrquestradorLucke.Infrastructure.Data;
using OrquestradorLucke.Infrastructure.Quota;
using OrquestradorLucke.Worker;

namespace OrquestradorLucke.Tests;

/// <summary>
/// Valida o grafo de injeção de dependência do daemon com a composição real do host: IOptions, um
/// expert por modelo do catálogo MoE, Circuit Breaker de cota persistido (Scoped), índice do RAG,
/// canal de gatilho da indexação e os dois BackgroundServices.
/// </summary>
/// <remarks>
/// A validação é a mesma que o host executa no start (<c>ValidateOnBuild</c> +
/// <c>ValidateScopes</c>): dependência não registrada ou serviço Scoped consumido por Singleton
/// (o caso clássico do <c>DbContext</c> preso ao tempo de vida do processo) falha aqui, e não no
/// primeiro ciclo do laço.
/// </remarks>
public sealed class OrchestratorCompositionTests
{
    [Fact]
    public void AddOrchestrator_DeveComporOGrafoCompletoSemDependenciaFaltante()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddOrchestrator(CreateConfiguration());

        // Registrados em Program: o consumidor do canal e o laço do orquestrador.
        services.AddHostedService<LuckeOrchestratorWorker>();
        services.AddHostedService<IndexingBackgroundService>();

        var act = () => DependencyInjectionSetup.ValidateOrchestratorComposition(services);

        act.Should().NotThrow();
    }

    [Fact]
    public void AddOrchestrator_DeveRegistrarOCircuitBreakerDeCotaPersistidoComoScoped()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddOrchestrator(CreateConfiguration());

        var descriptor = services.Single(candidate => candidate.ServiceType == typeof(IQuotaManager));

        descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
        descriptor.ImplementationType.Should().Be<DbQuotaManager>();
    }

    [Fact]
    public void AddOrchestrator_DeveRegistrarOCanalDeIndexacaoComoSingleton()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddOrchestrator(CreateConfiguration());

        var descriptor = services.Single(candidate => candidate.ServiceType == typeof(IndexingChannel));

        descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddOrchestrator_SemConnectionString_DeveFalharRapido()
    {
        var services = new ServiceCollection();

        var act = () => services.AddOrchestrator(new ConfigurationBuilder().Build());

        act.Should().Throw<InvalidOperationException>().WithMessage("*ConnectionStrings*");
    }

    /// <summary>Configuração mínima exigida pela composição: a connection string é obrigatória.</summary>
    private static IConfiguration CreateConfiguration()
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{AppDbContext.ConnectionStringName}"] =
                    "Host=localhost;Port=5432;Database=lucke_db;Username=lucke;Password=lucke",
                ["AiStudio:BaseUrl"] = "https://generativelanguage.googleapis.com/",
                ["AiStudio:ApiVersion"] = "v1beta",
                ["Frustration:MaxFailures"] = "3",
                ["Orchestrator:IndexingIntervalMinutes"] = "15"
            })
            .Build();
}
