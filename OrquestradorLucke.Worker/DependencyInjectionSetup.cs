using Microsoft.Extensions.Options;
using OrquestradorLucke.Application.Configuration;
using OrquestradorLucke.Application.Interfaces;
using OrquestradorLucke.Domain;
using OrquestradorLucke.Infrastructure.Adapters;
using OrquestradorLucke.Infrastructure.Configuration;
using OrquestradorLucke.Infrastructure.Quota;
using OrquestradorLucke.Worker.Configuration;
using Polly;

namespace OrquestradorLucke.Worker;

/// <summary>
/// Composição da injeção de dependência do host: bind dos IOptions, um expert do Google AI Studio
/// por modelo do catálogo MoE, Circuit Breaker de cota e política de resiliência (Polly).
/// </summary>
public static class DependencyInjectionSetup
{
    /// <summary>Registra as opções, os serviços de infraestrutura e o roteador MoE.</summary>
    /// <param name="services">Coleção de serviços do host.</param>
    /// <param name="configuration">Configuração da aplicação (appsettings/user-secrets/variáveis de ambiente).</param>
    public static IServiceCollection AddOrchestrator(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Bind das configurações (segredos vêm de user-secrets ou variáveis de ambiente - sem hardcoding).
        services.Configure<AiStudioOptions>(configuration.GetSection(AiStudioOptions.SectionName));
        services.Configure<HttpResilienceOptions>(configuration.GetSection(HttpResilienceOptions.SectionName));
        services.Configure<GitHubOptions>(configuration.GetSection(GitHubOptions.SectionName));
        services.Configure<FrustrationSettings>(configuration.GetSection(FrustrationSettings.SectionName));
        services.Configure<OrchestratorWorkerOptions>(configuration.GetSection(OrchestratorWorkerOptions.SectionName));

        // Circuit Breaker de cota: estado único e compartilhado por todo o host.
        services.AddSingleton<IQuotaManager, InMemoryQuotaManager>();

        AddAiStudioExperts(services, ReadResilienceOptions(configuration));

        // Serviços resolvidos a cada iteração (escopo do laço do BackgroundService).
        services.AddScoped<IGitHubService, GitHubAdapter>();
        services.AddScoped<ITaskRouter, MoETaskRouter>();

        return services;
    }

    /// <summary>
    /// Registra um expert do Google AI Studio para CADA modelo do <see cref="ModelCatalog"/>.
    /// Cada modelo recebe um Named Client próprio (sockets gerenciados pelo IHttpClientFactory) e a
    /// política Polly de retry para falhas transitórias.
    /// </summary>
    private static void AddAiStudioExperts(IServiceCollection services, HttpResilienceOptions resilience)
    {
        foreach (var modelName in ModelCatalog.AllModels)
        {
            services
                .AddHttpClient(modelName, ConfigureAiStudioClient)
                .AddTransientHttpErrorPolicy(policyBuilder => CreateTransientRetryPolicy(policyBuilder, resilience));

            services.AddTransient<ILLMProvider>(serviceProvider => CreateAiStudioExpert(serviceProvider, modelName));
        }
    }

    /// <summary>Configura o Named Client de um modelo: BaseAddress e timeout vindos de <see cref="AiStudioOptions"/>.</summary>
    private static void ConfigureAiStudioClient(IServiceProvider serviceProvider, HttpClient httpClient)
    {
        var aiStudioOptions = serviceProvider.GetRequiredService<IOptions<AiStudioOptions>>().Value;

        if (Uri.TryCreate(aiStudioOptions.BaseUrl, UriKind.Absolute, out var baseAddress))
        {
            httpClient.BaseAddress = baseAddress;
        }

        if (aiStudioOptions.TimeoutSeconds > 0)
        {
            httpClient.Timeout = TimeSpan.FromSeconds(aiStudioOptions.TimeoutSeconds);
        }
    }

    /// <summary>
    /// Cria o expert do modelo reaproveitando BaseUrl/ApiKey/timeouts da configuração (IOptions) e
    /// sobrescrevendo apenas o identificador do modelo.
    /// </summary>
    private static ILLMProvider CreateAiStudioExpert(IServiceProvider serviceProvider, string modelName)
    {
        var httpClient = serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(modelName);
        var template = serviceProvider.GetRequiredService<IOptions<AiStudioOptions>>().Value;

        return new GoogleAiStudioAdapter(
            httpClient,
            Options.Create(template.ForModel(modelName)),
            serviceProvider.GetRequiredService<IQuotaManager>());
    }

    /// <summary>
    /// Retry com backoff exponencial para falhas transitórias (5xx, 408 e
    /// <see cref="HttpRequestException"/>, cobertas por <c>AddTransientHttpErrorPolicy</c>).
    /// </summary>
    /// <remarks>
    /// <see cref="QuotaExhaustedException"/> NÃO é tratada por esta política: ela não deriva de
    /// <see cref="HttpRequestException"/> nem corresponde a resposta transitória. Cota esgotada é
    /// resolvida pelo Circuit Breaker de cota e pelo fallback do roteador MoE, não por nova
    /// tentativa no mesmo modelo.
    /// </remarks>
    private static IAsyncPolicy<HttpResponseMessage> CreateTransientRetryPolicy(
        PolicyBuilder<HttpResponseMessage> policyBuilder,
        HttpResilienceOptions resilience)
    {
        var attempts = Math.Max(1, resilience.RetryAttempts);
        var baseDelaySeconds = Math.Max(1, resilience.BaseDelaySeconds);

        return policyBuilder.WaitAndRetryAsync(
            attempts,
            attempt => TimeSpan.FromSeconds(baseDelaySeconds * Math.Pow(2, attempt - 1)));
    }

    /// <summary>
    /// Lê os parâmetros de resiliência na composição: a extensão <c>AddTransientHttpErrorPolicy</c>
    /// do Polly (v7) não oferece overload com <see cref="IServiceProvider"/>, portanto a seção é
    /// bindada aqui — da mesma fonte registrada em <see cref="HttpResilienceOptions"/>.
    /// </summary>
    private static HttpResilienceOptions ReadResilienceOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(HttpResilienceOptions.SectionName);

        return section.Exists()
            ? section.Get<HttpResilienceOptions>() ?? new HttpResilienceOptions()
            : new HttpResilienceOptions();
    }
}
