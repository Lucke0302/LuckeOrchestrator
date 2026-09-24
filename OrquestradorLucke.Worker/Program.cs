using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OrquestradorLucke.Application.Services;
using OrquestradorLucke.Worker;

// Host web (Kestrel) + BackgroundService: o daemon mantém o laço do orquestrador e passa a expor um
// endpoint HTTP — o webhook do GitHub dispara a indexação sob demanda, no lugar do polling.
var builder = WebApplication.CreateBuilder(args);

// Daemon no Linux: habilita o Type=notify (sd_notify) e o formatter do journald. A extensão é
// context-aware — só ativa quando o processo roda sob systemd (ou NOTIFY_SOCKET no Unix), mantendo
// o lifetime de console (Ctrl+C, shutdown gracioso) em dev/execução manual.
builder.Services.AddSystemd();

// Composição da injeção de dependência: IOptions, um expert do Google AI Studio por modelo do
// catálogo MoE, o expert de embeddings do RAG, Circuit Breaker de cota (Singleton), política de
// resiliência (Polly) e a persistência PostgreSQL/pgvector.
builder.Services.AddOrchestrator(builder.Configuration);

// Valida o grafo de DI antes de subir: dependência não registrada (ou serviço Scoped consumido pela
// raiz) falha aqui, no start do daemon, em vez de aparecer só no journal da primeira iteração.
DependencyInjectionSetup.ValidateOrchestratorComposition(builder.Services);

builder.Services.AddHostedService<LuckeOrchestratorWorker>();

var app = builder.Build();

// Webhook do GitHub: substitui o polling do indexador (Orchestrator:IndexingIntervalMinutes) por um
// gancho pós-merge. Responde 202 imediatamente — o GitHub cancela entregas que passam de ~10 s — e
// sincroniza o índice em background, em um escopo próprio (o CodebaseIndexerService é Scoped).
app.MapPost("/api/webhook/github", (IServiceScopeFactory scopeFactory, ILoggerFactory loggerFactory) =>
{
    _ = SynchronizeCodebaseIndexAsync(
        scopeFactory,
        loggerFactory.CreateLogger("OrquestradorLucke.Webhook.GitHub"));

    return Results.Accepted();
});

app.Run();

/// <summary>
/// Sincroniza o índice vetorial do RAG em background, no escopo criado para a execução do webhook.
/// </summary>
/// <remarks>
/// O disparo é "fire-and-forget" de propósito: o GitHub espera a resposta em segundos e a indexação
/// é best-effort — o índice atual continua servindo de contexto e o próximo ciclo (intervalo ou novo
/// webhook) reindexa o que faltou. O <see cref="IServiceScopeFactory"/> é obrigatório porque o
/// <see cref="CodebaseIndexerService"/> e o <c>DbContext</c> que ele consome são Scoped.
/// </remarks>
static async Task SynchronizeCodebaseIndexAsync(IServiceScopeFactory scopeFactory, ILogger logger)
{
    try
    {
        using var scope = scopeFactory.CreateScope();

        var codeIndexer = scope.ServiceProvider.GetRequiredService<CodebaseIndexerService>();

        var result = await codeIndexer
            .IndexChangedDocumentsAsync(CancellationToken.None)
            .ConfigureAwait(false);

        logger.LogInformation(
            "Índice do RAG sincronizado pelo webhook: {Indexed} documento(s) novos/alterados entre {Discovered} arquivo(s) C#{Skipped}.",
            result.IndexedDocuments,
            result.DiscoveredFiles,
            result.SkippedDocuments > 0
                ? $"; {result.SkippedDocuments} descartado(s) por não gerarem embedding"
                : string.Empty);
    }
    catch (Exception ex)
    {
        // Nada é propagado: a entrega do webhook já foi aceita e a indexação é enriquecimento do RAG.
        logger.LogError(ex, "Falha ao sincronizar o índice do RAG pelo webhook do GitHub.");
    }
}


