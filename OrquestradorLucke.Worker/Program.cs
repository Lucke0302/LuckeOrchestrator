using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OrquestradorLucke.Worker;
using OrquestradorLucke.Worker.Configuration;
using OrquestradorLucke.Worker.Endpoints;
using OrquestradorLucke.Worker.Logging;

// Host web (Kestrel) + BackgroundService: o daemon mantém o laço do orquestrador e passa a ser o
// back-end em tempo real da aplicação — webhook do GitHub, API de gerenciamento/revisão e streaming
// de logs pelo SignalR (o painel web não depende mais do DBeaver nem do journalctl).
var builder = WebApplication.CreateBuilder(args);

// Chave de configuração do segredo compartilhado com o GitHub e nome do cabeçalho de assinatura:
// nenhum dos dois é hardcoded — o segredo vem de user-secrets/variável de ambiente.
const string GitHubWebhookSecretKey = "GitHub:WebhookSecret";
const string HubSignatureHeaderName = "X-Hub-Signature-256";
const string SignaturePrefix = "sha256=";

// Daemon no Linux: habilita o Type=notify (sd_notify) e o formatter do journald. A extensão é
// context-aware — só ativa quando o processo roda sob systemd (ou NOTIFY_SOCKET no Unix), mantendo
// o lifetime de console (Ctrl+C, shutdown gracioso) em dev/execução manual.
builder.Services.AddSystemd();

// Composição da injeção de dependência: IOptions, um expert do Google AI Studio por modelo do
// catálogo MoE, o expert de embeddings do RAG, Circuit Breaker de cota persistido (Scoped), política
// de resiliência (Polly), a persistência PostgreSQL/pgvector, o medidor de frustração do daemon e o
// canal de gatilho da indexação.
builder.Services.AddOrchestrator(builder.Configuration);

// API de gerenciamento/revisão (Minimal APIs), streaming de logs (SignalR) e CORS do painel web.
builder.Services.AddManagementApi(builder.Configuration);

// Laço do orquestrador (fila → expert → branch/commit/PR), consumidor do gatilho de indexação (uma
// indexação por vez) e publicador do log no hub (consumidor do canal do streaming).
builder.Services.AddHostedService<LuckeOrchestratorWorker>();
builder.Services.AddHostedService<IndexingBackgroundService>();
builder.Services.AddHostedService<LogBroadcastService>();

// Valida o grafo de DI antes de subir: dependência não registrada (ou serviço Scoped consumido pela
// raiz) falha aqui, no start do daemon, em vez de aparecer só no journal da primeira iteração.
DependencyInjectionSetup.ValidateOrchestratorComposition(builder.Services);

var app = builder.Build();

// CORS antes dos endpoints: o painel web (outra origem) alcança a API e o negotiate do hub. As
// origens vêm de Cors:AllowedOrigins — sem origem liberada, o navegador simplesmente é bloqueado.
app.UseCors(CorsSettings.PolicyName);

// Webhook do GitHub: substitui o polling do indexador (Orchestrator:IndexingIntervalMinutes) por um
// gancho pós-merge. Valida o HMAC-SHA256 do corpo bruto (X-Hub-Signature-256) contra o segredo
// configurado, responde 401 quando a assinatura não confere e, quando confere, publica o gatilho no
// IndexingChannel e responde 202 imediatamente — quem indexa é o IndexingBackgroundService.
app.MapPost("/api/webhook/github", async (
    HttpContext context,
    IConfiguration configuration,
    IndexingChannel indexingChannel,
    ILoggerFactory loggerFactory) =>
{
    var logger = loggerFactory.CreateLogger("OrquestradorLucke.Webhook.GitHub");
    var webhookSecret = configuration[GitHubWebhookSecretKey];

    if (string.IsNullOrWhiteSpace(webhookSecret))
    {
        // Fail closed: sem segredo não há como autenticar a entrega — o gatilho não é aceito.
        logger.LogError(
            "Webhook do GitHub recusado: a chave de configuração '{ConfigurationKey}' não está definida.",
            GitHubWebhookSecretKey);

        return Results.StatusCode(StatusCodes.Status500InternalServerError);
    }

    // O HMAC é calculado sobre os BYTES do corpo: ler como string e recodificar poderia alterar o
    // payload (BOM, normalização de quebras de linha) e invalidar a assinatura de uma entrega legítima.
    using var bodyBuffer = new MemoryStream();

    await context.Request.Body.CopyToAsync(bodyBuffer, context.RequestAborted);

    var signature = context.Request.Headers[HubSignatureHeaderName].ToString();

    if (!IsSignatureValid(webhookSecret, bodyBuffer.ToArray(), signature))
    {
        logger.LogWarning("Webhook do GitHub recusado: assinatura HMAC-SHA256 ausente ou inválida.");

        return Results.Unauthorized();
    }

    // Debounce: com um pedido já na fila, o TryWrite é descartado (DropWrite) — cinco entregas em
    // rajada viram uma única indexação, e a resposta sai antes de qualquer trabalho pesado.
    indexingChannel.TryWrite(true);

    return Results.Accepted();
});

// API de gerenciamento/revisão do painel web: listar, enfileirar, aprovar (merge com o AdminToken) e
// rejeitar (fecha o PR, alimenta o medidor de frustração e devolve a tarefa para Pendente).
app.MapTaskEndpoints();

// Streaming de logs: o hub entrega ao painel o log da aplicação em tempo real (o provider capturado
// em AddManagementApi alimenta o canal que o LogBroadcastService publica aqui).
app.MapHub<LogHub>(LogStreamContract.HubRoute);

app.Run();

/// <summary>
/// Valida a assinatura HMAC-SHA256 do corpo bruto (<c>X-Hub-Signature-256: sha256=&lt;hex&gt;</c>)
/// contra o segredo configurado.
/// </summary>
/// <remarks>
/// A comparação usa <see cref="CryptographicOperations.FixedTimeEquals"/>: comparar as strings com
/// <c>==</c> vazaria o tempo de resposta por caractere e permitiria descobrir a assinatura correta
/// por tentativa e erro.
/// </remarks>
/// <param name="secret">Segredo compartilhado com o GitHub (<c>GitHub:WebhookSecret</c>).</param>
/// <param name="body">Bytes do corpo bruto da requisição.</param>
/// <param name="signatureHeader">Valor do cabeçalho <c>X-Hub-Signature-256</c>.</param>
/// <returns><c>true</c> apenas quando a assinatura confere com o HMAC do corpo.</returns>
static bool IsSignatureValid(string secret, byte[] body, string signatureHeader)
{
    if (string.IsNullOrWhiteSpace(signatureHeader)
        || !signatureHeader.StartsWith(SignaturePrefix, StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    byte[] providedSignature;

    try
    {
        providedSignature = Convert.FromHexString(signatureHeader[SignaturePrefix.Length..].Trim());
    }
    catch (FormatException)
    {
        // Assinatura que não é hexadecimal não tem como conferir: entrega recusada.
        return false;
    }

    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));

    return CryptographicOperations.FixedTimeEquals(hmac.ComputeHash(body), providedSignature);
}


