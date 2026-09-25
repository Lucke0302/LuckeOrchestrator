using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OrquestradorLucke.Application.Configuration;
using OrquestradorLucke.Application.Interfaces;
using OrquestradorLucke.Application.Services;
using OrquestradorLucke.Domain;
using OrquestradorLucke.Infrastructure.Adapters;
using OrquestradorLucke.Infrastructure.Configuration;
using OrquestradorLucke.Infrastructure.Data;
using OrquestradorLucke.Infrastructure.Data.Repositories;
using OrquestradorLucke.Infrastructure.Quota;
using OrquestradorLucke.Infrastructure.Security;
using OrquestradorLucke.Worker.Configuration;
using OrquestradorLucke.Worker.Logging;
using OrquestradorLucke.Worker.Services;
using Polly;

namespace OrquestradorLucke.Worker;

/// <summary>
/// Composição da injeção de dependência do host: bind dos IOptions, persistência PostgreSQL
/// (EF Core + Npgsql + pgvector), um expert do Google AI Studio por modelo do catálogo MoE,
/// Circuit Breaker de cota, política de resiliência (Polly), o medidor de frustração compartilhado, a
/// autenticação JWT das rotas administrativas e a API de gerenciamento/revisão com o streaming de logs
/// pelo SignalR.
/// </summary>
public static class DependencyInjectionSetup
{
    /// <summary>
    /// Nome do parâmetro de query que carrega o access token nas conexões do hub de logs: o WebSocket
    /// do SignalR não envia o cabeçalho <c>Authorization</c>, então o token (15 minutos) vai na query
    /// string e é extraído no evento <c>OnMessageReceived</c> do <c>JwtBearer</c>.
    /// </summary>
    public const string AccessTokenQueryParameterName = "access_token";

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

        // Persistência: a connection string vem da configuração (user-secrets/variável de ambiente) e o
        // EF Core (Npgsql + pgvector) é registrado pela Infrastructure, que é a única camada que o conhece.
        var connectionString = configuration.GetConnectionString(AppDbContext.ConnectionStringName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{AppDbContext.ConnectionStringName} não configurada. " +
                $"Defina via user-secrets ou variável de ambiente (ConnectionStrings__{AppDbContext.ConnectionStringName}).");
        }

        services.AddPostgresPersistence(connectionString);

        // Circuit Breaker de cota: persistido no PostgreSQL (tabela quota_states) e resolvido no
        // escopo da iteração, junto do AppDbContext — o bloqueio aplicado em um 429 sobrevive ao
        // reinício do daemon e vale para todas as instâncias que apontam para o mesmo banco.
        services.AddScoped<IQuotaManager, DbQuotaManager>();

        // Medidor de frustração do daemon (Singleton): o laço registra falhas de geração/entrega e a
        // revisão humana registra as rejeições — as duas portas alimentam o MESMO contador, que é o
        // gatilho do overdrive. Vive na DI (e não em um campo do BackgroundService) justamente porque
        // a API de review precisa da mesma instância que o laço.
        services.AddSingleton(serviceProvider => new FrustrationTracker(
            Math.Max(1, serviceProvider.GetRequiredService<IOptions<FrustrationSettings>>().Value.MaxFailures)));

        var resilience = ReadResilienceOptions(configuration);

        AddAiStudioExperts(services, resilience);

        // Expert de embeddings do RAG: o mesmo adapter do AI Studio, apontado para o modelo de
        // embeddings do catálogo (que não participa das cadeias MoE) e com Named Client próprio.
        // A cota é contabilizada por modelo, então um 429 nos embeddings não bloqueia a geração.
        services
            .AddHttpClient(ModelCatalog.GeminiEmbedding2, ConfigureAiStudioClient)
            .AddTransientHttpErrorPolicy(policyBuilder => CreateTransientRetryPolicy(policyBuilder, resilience));

        services.AddTransient<IEmbeddingProvider>(
            serviceProvider => CreateAiStudioExpert(serviceProvider, ModelCatalog.GeminiEmbedding2));

        // Serviços resolvidos a cada iteração (escopo do laço do BackgroundService).
        services.AddScoped<IGitHubService, GitHubAdapter>();
        services.AddScoped<IAgentTaskRepository, AgentTaskRepository>();
        services.AddScoped<ITaskRouter, MoETaskRouter>();

        // RAG: índice vetorial (pgvector) e indexador incremental da base de código.
        services.AddScoped<ICodeContextRepository, CodeContextRepository>();
        services.AddScoped<CodebaseIndexerService>();

        // Gatilho da indexação: canal Singleton (capacidade 1, DropWrite) que o webhook alimenta e o
        // IndexingBackgroundService consome — uma rajada de entregas vira um único pedido pendente e
        // o PostgreSQL recebe uma conexão por vez.
        services.AddSingleton<IndexingChannel>();

        return services;
    }

    /// <summary>
    /// Registra a API de gerenciamento/revisão (Minimal APIs), o streaming de logs pelo SignalR e o
    /// CORS do painel web. Depende de <see cref="AddOrchestrator"/> (persistência, revisão e o medidor
    /// de frustração) e é o que transforma o daemon no back-end em tempo real da aplicação.
    /// </summary>
    /// <param name="services">Coleção de serviços do host.</param>
    /// <param name="configuration">Configuração da aplicação (appsettings/variáveis de ambiente).</param>
    public static IServiceCollection AddManagementApi(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<LogStreamingOptions>(configuration.GetSection(LogStreamingOptions.SectionName));
        services.Configure<CorsSettings>(configuration.GetSection(CorsSettings.SectionName));

        // Streaming de logs: o provider entra no pipeline de ILogger (ao lado de console/journald) e
        // entrega os eventos a um canal não bloqueante; quem publica no hub é o BackgroundService.
        services.AddSingleton<SignalRLogSink>();
        services.AddSingleton<ILoggerProvider, SignalRLoggerProvider>();

        // SignalR + protocolo JSON explícito: camelCase e nível do log como texto ("Information"), para
        // que o contrato do fio não dependa dos defaults do framework.
        services
            .AddSignalR()
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            });

        // Casos de uso de revisão (accept/reject) e criação de tarefas: Scoped, junto do repositório e
        // do adapter do GitHub que ele consome.
        services.AddScoped<TaskReviewService>();

        // Motor de chat do painel: typed client (o IHttpClientFactory é quem gerencia os sockets). A
        // resiliência — retry, fallback de modelo e contrato das tags de raciocínio — é interna ao
        // serviço, que por isso NÃO recebe a política Polly dos experts MoE.
        services.AddHttpClient<GeminiChatService>();

        // CORS: o painel web roda em outra origem (localhost em desenvolvimento) e precisa alcançar a
        // API e o negotiate do hub. As origens vêm da configuração — nada hardcoded.
        var cors = ReadCorsSettings(configuration);

        services.AddCors(options => options.AddPolicy(CorsSettings.PolicyName, policy =>
        {
            policy.AllowAnyHeader().AllowAnyMethod().AllowCredentials();

            if (cors.AllowedOrigins.Length > 0)
            {
                policy.WithOrigins(cors.AllowedOrigins);
            }
        }));

        return services;
    }

    /// <summary>
    /// Registra a autenticação JWT (access token de 15 minutos, refresh de 7 dias) e a autorização das
    /// rotas administrativas: o token é validado com o segredo de <c>Jwt:Secret</c> e, no hub do
    /// SignalR, também aceito pela query string <c>access_token</c> — o WebSocket não envia cabeçalhos.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Depende da persistência registrada em <see cref="AddOrchestrator"/> (o repositório de contas) e é
    /// composta antes de <c>UseAuthentication</c>/<c>UseAuthorization</c> no pipeline. A configuração é
    /// validada na composição: sem segredo utilizável o host falha no start, e não no primeiro login.
    /// </para>
    /// <para>
    /// A mesma instância de <see cref="JwtOptions"/> é oferecida de duas formas: <c>IOptions&lt;T&gt;</c>
    /// para a Infrastructure (padrão do projeto) e como objeto direto para a Application, que não
    /// referencia o pacote de Options — assim há uma única fonte de verdade para o segredo e as duas
    /// validades.
    /// </para>
    /// </remarks>
    /// <param name="services">Coleção de serviços do host.</param>
    /// <param name="configuration">Configuração da aplicação (appsettings/user-secrets/variáveis de ambiente).</param>
    public static IServiceCollection AddJwtAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var jwt = ReadJwtOptions(configuration);

        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.AddSingleton(jwt);

        // Credenciais do usuário inicial (seed do primeiro start): a instância direta segue o mesmo
        // motivo do JwtOptions — a Application não conhece o pacote de Options.
        services.AddSingleton(ReadBootstrapSettings(configuration));

        // Autenticação: o provedor de token é stateless (Singleton); o repositório de contas e os casos
        // de uso são Scoped, junto do DbContext do escopo da requisição HTTP.
        services.AddSingleton<IAccessTokenProvider, JwtTokenProvider>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<AuthenticationService>();
        services.AddScoped<AuthBootstrapService>();

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret)),
                    ValidateLifetime = true,
                    // Sem tolerância: os 5 minutos de skew padrão estenderiam um token de 15 minutos para
                    // 20 — o cliente renova pelo /api/auth/refresh, então não há folga a dar.
                    ClockSkew = TimeSpan.Zero,
                    NameClaimType = JwtRegisteredClaimNames.UniqueName
                };

                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        // O WebSocket do SignalR não carrega o cabeçalho Authorization: o token chega na
                        // query string (?access_token=...). A leitura é restrita à rota do hub para que um
                        // access_token perdido em uma chamada de API não autentique por acidente.
                        var accessToken = context.Request.Query[AccessTokenQueryParameterName];

                        if (!string.IsNullOrEmpty(accessToken)
                            && context.HttpContext.Request.Path.StartsWithSegments(LogStreamContract.HubRoute))
                        {
                            context.Token = accessToken;
                        }

                        return Task.CompletedTask;
                    }
                };
            });

        services.AddAuthorization();

        return services;
    }

    /// <summary>
    /// Lê e valida as opções de JWT na composição — o segredo e as validades são exigidos antes de o
    /// host subir, não na primeira autenticação.
    /// </summary>
    private static JwtOptions ReadJwtOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(JwtOptions.SectionName);
        var jwt = section.Exists() ? section.Get<JwtOptions>() ?? new JwtOptions() : new JwtOptions();

        jwt.EnsureUsable();

        return jwt;
    }

    /// <summary>
    /// Lê as credenciais do usuário inicial (seção <c>Auth</c>). Uma seção ausente ou vazia é válida: o
    /// seed simplesmente não cria conta nenhuma.
    /// </summary>
    private static AuthBootstrapSettings ReadBootstrapSettings(IConfiguration configuration)
    {
        var section = configuration.GetSection(AuthBootstrapSettings.SectionName);

        return section.Exists()
            ? section.Get<AuthBootstrapSettings>() ?? new AuthBootstrapSettings()
            : new AuthBootstrapSettings();
    }

    /// <summary>
    /// Valida o grafo de injeção de dependência antes de o host subir: dependências não registradas e
    /// serviços Scoped resolvidos a partir da raiz falham no start do daemon, não na primeira iteração
    /// do laço.
    /// </summary>
    /// <remarks>
    /// O host só habilita essa checagem automaticamente em Development; sob systemd (Production) o
    /// erro apareceria apenas no journal. Nada é instanciado — as call sites são validadas e o
    /// provider criado aqui é descartado na sequência, sem relação com o container do host.
    /// </remarks>
    /// <param name="services">Coleção de serviços composta por <see cref="AddOrchestrator"/>.</param>
    public static void ValidateOrchestratorComposition(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services
            .BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true
            })
            .Dispose();
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
    /// <remarks>
    /// O tipo concreto é devolvido (em vez de <see cref="ILLMProvider"/>) porque o mesmo adapter
    /// atende às duas operações do AI Studio: geração de conteúdo (MoE) e embeddings (RAG) — o
    /// registro de <see cref="IEmbeddingProvider"/> reutiliza esta fábrica com o modelo de embeddings.
    /// O logger é resolvido do container para que a auditoria do parse (contagem de arquivos e prévia
    /// da resposta) apareça no log do host, e não em uma implementação silenciosa.
    /// </remarks>
    private static GoogleAiStudioAdapter CreateAiStudioExpert(IServiceProvider serviceProvider, string modelName)
    {
        var httpClient = serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(modelName);
        var template = serviceProvider.GetRequiredService<IOptions<AiStudioOptions>>().Value;

        return new GoogleAiStudioAdapter(
            httpClient,
            Options.Create(template.ForModel(modelName)),
            serviceProvider.GetRequiredService<IQuotaManager>(),
            serviceProvider.GetRequiredService<ILogger<GoogleAiStudioAdapter>>());
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

    /// <summary>
    /// Lê as origens liberadas no CORS na composição (a política é construída antes do host subir):
    /// espaços são removidos, entradas vazias são descartadas e a lista é deduplicada — uma vírgula a
    /// mais no appsettings (ou na variável de ambiente) não derruba o start.
    /// </summary>
    private static CorsSettings ReadCorsSettings(IConfiguration configuration)
    {
        var section = configuration.GetSection(CorsSettings.SectionName);
        var settings = section.Exists() ? section.Get<CorsSettings>() ?? new CorsSettings() : new CorsSettings();

        settings.AllowedOrigins = ReadAllowedOrigins(section).ToArray();

        return settings;
    }

    /// <summary>
    /// Extrai as origens da seção <c>Cors</c> somando as duas formas de configuração possíveis: os
    /// itens do array do <c>appsettings.json</c> e o valor escalar de uma variável de ambiente — que
    /// não tem como representar um array e impõe uma única string separada por vírgula
    /// (<c>Cors__AllowedOrigins=https://painel.vercel.app,http://localhost:4173</c>, o caminho para
    /// acrescentar a URL do painel publicado sem tocar no código). As duas fontes passam pelo mesmo
    /// recorte, então uma origem repetida entre elas entra uma única vez.
    /// </summary>
    private static IEnumerable<string> ReadAllowedOrigins(IConfiguration section)
    {
        var originsSection = section.GetSection(nameof(CorsSettings.AllowedOrigins));

        var configuredValues = originsSection.GetChildren()
            .Select(child => child.Value)
            .Append(originsSection.Value);

        return configuredValues
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value => value!.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
