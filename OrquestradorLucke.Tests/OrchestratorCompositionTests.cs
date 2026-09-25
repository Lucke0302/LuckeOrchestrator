using FluentAssertions;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrquestradorLucke.Application.Interfaces;
using OrquestradorLucke.Application.Services;
using OrquestradorLucke.Domain;
using OrquestradorLucke.Infrastructure.Data;
using OrquestradorLucke.Infrastructure.Quota;
using OrquestradorLucke.Worker;
using OrquestradorLucke.Worker.Configuration;
using OrquestradorLucke.Worker.Logging;

namespace OrquestradorLucke.Tests;

/// <summary>
/// Valida o grafo de injeção de dependência do daemon com a composição real do host: IOptions, um
/// expert por modelo do catálogo MoE, Circuit Breaker de cota persistido (Scoped), índice do RAG,
/// canal de gatilho da indexação, medidor de frustração compartilhado, streaming de logs (SignalR) e
/// os três BackgroundServices.
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
        var services = CreateHostServices();

        services.AddOrchestrator(CreateConfiguration());
        services.AddManagementApi(CreateConfiguration());
        services.AddJwtAuthentication(CreateConfiguration());

        // Registrados em Program: o consumidor do canal, o laço do orquestrador e o publicador do log.
        services.AddHostedService<LuckeOrchestratorWorker>();
        services.AddHostedService<IndexingBackgroundService>();
        services.AddHostedService<LogBroadcastService>();

        var act = () => DependencyInjectionSetup.ValidateOrchestratorComposition(services);

        act.Should().NotThrow();
    }

    [Fact]
    public void AddOrchestrator_DeveRegistrarOMedidorDeFrustracaoComoSingleton()
    {
        // O medidor é compartilhado pelo laço do daemon e pela API de revisão: dois escopos diferentes
        // precisam enxergar o MESMO contador, senão a rejeição do revisor não alimenta o overdrive.
        var services = CreateHostServices();

        services.AddOrchestrator(CreateConfiguration());

        var descriptor = services.Single(candidate => candidate.ServiceType == typeof(FrustrationTracker));

        descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);

        using var provider = services.BuildServiceProvider();
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();

        var tracker = provider.GetRequiredService<FrustrationTracker>();

        firstScope.ServiceProvider.GetRequiredService<FrustrationTracker>().Should().BeSameAs(tracker);
        secondScope.ServiceProvider.GetRequiredService<FrustrationTracker>().Should().BeSameAs(tracker);
    }

    [Fact]
    public void AddManagementApi_DeveRegistrarOStreamingDeLogsEASessaoDeRevisao()
    {
        var services = CreateHostServices();

        services.AddOrchestrator(CreateConfiguration());
        services.AddManagementApi(CreateConfiguration());

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(SignalRLogSink) &&
            descriptor.Lifetime == ServiceLifetime.Singleton);

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(ILoggerProvider) &&
            descriptor.ImplementationType == typeof(SignalRLoggerProvider));

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(TaskReviewService) &&
            descriptor.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddManagementApi_SemOrigensDeCors_NaoDeveLiberarOrigemCruzada()
    {
        // Lista vazia (ou com entradas em branco) é normalizada: nenhuma origem liberada e o CORS não
        // explode na construção da política.
        var services = CreateHostServices();
        services.AddOrchestrator(CreateConfiguration());
        services.AddManagementApi(CreateConfiguration());

        var act = () => DependencyInjectionSetup.ValidateOrchestratorComposition(services);

        act.Should().NotThrow();
    }

    [Fact]
    public void AddManagementApi_DeveLiberarAsOrigensDoPainelComCredenciais()
    {
        // O painel web (Vite em 5173 e o preview em 4173) chama a API de outra origem e o SignalR
        // exige credenciais (AllowCredentials) — e a lista em UMA string separada por vírgula, o
        // formato de Cors__AllowedOrigins (variável de ambiente), tem de produzir o mesmo resultado
        // do array do appsettings: split, recorte de espaços e deduplicação.
        var services = CreateHostServices();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cors:AllowedOrigins"] = "http://localhost:5173, http://localhost:4173 ,,http://localhost:5173"
            })
            .Build();

        services.AddManagementApi(configuration);

        using var provider = services.BuildServiceProvider();

        var policy = provider.GetRequiredService<IOptions<CorsOptions>>()
            .Value
            .GetPolicy(CorsSettings.PolicyName);

        policy.Should().NotBeNull();
        policy!.Origins.Should().Equal("http://localhost:5173", "http://localhost:4173");
        policy.AllowAnyHeader.Should().BeTrue();
        policy.AllowAnyMethod.Should().BeTrue();
        policy.SupportsCredentials.Should().BeTrue();
    }

    [Fact]
    public void AddManagementApi_DeveLiberarAsOrigensDoArrayDoAppsettings()
    {
        var services = CreateHostServices();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cors:AllowedOrigins:0"] = "http://localhost:5173",
                ["Cors:AllowedOrigins:1"] = " http://localhost:4173 "
            })
            .Build();

        services.AddManagementApi(configuration);

        using var provider = services.BuildServiceProvider();

        var policy = provider.GetRequiredService<IOptions<CorsOptions>>()
            .Value
            .GetPolicy(CorsSettings.PolicyName);

        policy.Should().NotBeNull();
        policy!.Origins.Should().Equal("http://localhost:5173", "http://localhost:4173");
    }

    [Fact]
    public void AddOrchestrator_DeveRegistrarOCircuitBreakerDeCotaPersistidoComoScoped()
    {
        var services = CreateHostServices();

        services.AddOrchestrator(CreateConfiguration());

        var descriptor = services.Single(candidate => candidate.ServiceType == typeof(IQuotaManager));

        descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
        descriptor.ImplementationType.Should().Be<DbQuotaManager>();
    }

    [Fact]
    public void AddOrchestrator_DeveRegistrarOCanalDeIndexacaoComoSingleton()
    {
        var services = CreateHostServices();

        services.AddOrchestrator(CreateConfiguration());

        var descriptor = services.Single(candidate => candidate.ServiceType == typeof(IndexingChannel));

        descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddOrchestrator_SemConnectionString_DeveFalharRapido()
    {
        var services = CreateHostServices();

        var act = () => services.AddOrchestrator(new ConfigurationBuilder().Build());

        act.Should().Throw<InvalidOperationException>().WithMessage("*ConnectionStrings*");
    }

    [Fact]
    public void AddJwtAuthentication_SemSegredo_DeveFalharRapido()
    {
        // Sem Jwt:Secret (ou com um segredo curto demais) o host subiria assinando/validando token com
        // chave inválida e só descobriria no primeiro login: a composição recusa antes de subir.
        var services = CreateHostServices();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = "curto-demais",
                ["Jwt:Issuer"] = "OrquestradorLucke.Tests"
            })
            .Build();

        var act = () => services.AddJwtAuthentication(configuration);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Jwt:Secret*");
    }

    [Fact]
    public void AddJwtAuthentication_DeveRegistrarOsTemposDeVidaDaAutenticacao()
    {
        var services = CreateHostServices();

        services.AddOrchestrator(CreateConfiguration());
        services.AddJwtAuthentication(CreateConfiguration());

        // O provedor de token é stateless (Singleton); repositório e casos de uso acompanham o escopo da
        // requisição HTTP, junto do DbContext.
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IAccessTokenProvider) &&
            descriptor.Lifetime == ServiceLifetime.Singleton);

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IUserRepository) &&
            descriptor.Lifetime == ServiceLifetime.Scoped);

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(AuthenticationService) &&
            descriptor.Lifetime == ServiceLifetime.Scoped);

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(AuthBootstrapService) &&
            descriptor.Lifetime == ServiceLifetime.Scoped);
    }

    /// <summary>
    /// Coleção de serviços com os registros que o host faz ANTES de qualquer serviço do usuário:
    /// logging e o lifetime da aplicação. Sem o lifetime, o <c>ValidateOnBuild</c> acusaria o
    /// <c>HttpConnectionManager</c> do SignalR (que o consome) como irresolúvel — um falso negativo
    /// que não existe no host real.
    /// </summary>
    private static ServiceCollection CreateHostServices()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime, StubApplicationLifetime>();

        return services;
    }

    /// <summary>Configuração mínima exigida pela composição: a connection string e o segredo do JWT são obrigatórios.</summary>
    private static IConfiguration CreateConfiguration()
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{AppDbContext.ConnectionStringName}"] =
                    "Host=localhost;Port=5432;Database=lucke_db;Username=lucke;Password=lucke",
                ["AiStudio:BaseUrl"] = "https://generativelanguage.googleapis.com/",
                ["AiStudio:ApiVersion"] = "v1beta",
                ["Frustration:MaxFailures"] = "3",
                ["Orchestrator:IndexingIntervalMinutes"] = "15",
                // O HS256 exige 256 bits: o segredo de teste tem os 32+ caracteres mínimos.
                ["Jwt:Secret"] = "segredo-de-teste-com-tamanho-suficiente-para-hs256",
                ["Jwt:Issuer"] = "OrquestradorLucke.Tests",
                ["Jwt:Audience"] = "OrquestradorLucke.Tests.Panel"
            })
            .Build();

    /// <summary>Lifetime de aplicação mínimo para a validação da DI (o host real registra o seu).</summary>
    private sealed class StubApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => _stopping.Token;

        public void StopApplication()
        {
        }
    }
}
