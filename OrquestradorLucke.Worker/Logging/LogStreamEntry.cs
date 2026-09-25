using Microsoft.Extensions.Logging;

namespace OrquestradorLucke.Worker.Logging;

/// <summary>
/// Contrato do streaming de logs (SignalR). Está em um único lugar de propósito: o front-end depende
/// destes nomes, e renomear um evento aqui sem ajustar o cliente quebraria o painel silenciosamente.
/// </summary>
/// <remarks>
/// <para>Rota do hub: <see cref="HubRoute"/> (<c>/hubs/logs</c>).</para>
/// <list type="bullet">
/// <item><description><b>server → client <c>log</c></b>: um <see cref="LogStreamEntry"/> por evento.</description></item>
/// <item><description><b>server → client <c>history</c></b>: lista de <see cref="LogStreamEntry"/> enviada ao próprio cliente que acabou de conectar (retrovisor).</description></item>
/// </list>
/// <para>
/// O JSON é serializado em <c>camelCase</c> com o nível como texto
/// (<c>{ "timestampUtc": ..., "level": "Information", "category": ..., "message": ..., "exception": ... }</c>),
/// configurado em <c>AddManagementApi</c>.
/// </para>
/// </remarks>
public static class LogStreamContract
{
    /// <summary>Rota do hub de logs.</summary>
    public const string HubRoute = "/hubs/logs";

    /// <summary>Evento com um evento de log individual.</summary>
    public const string LogEventName = "log";

    /// <summary>Evento com o retrovisor de eventos recentes, enviado ao cliente que conectou.</summary>
    public const string HistoryEventName = "history";
}

/// <summary>
/// Evento de log publicado no hub: o recorte do <see cref="ILogger"/> que o painel web precisa para
/// mostrar o que o daemon está fazendo.
/// </summary>
/// <param name="TimestampUtc">Instante do evento (UTC).</param>
/// <param name="Level">Nível do log (serializado como texto: <c>Information</c>, <c>Warning</c>, ...).</param>
/// <param name="Category">Categoria do logger (ex.: <c>OrquestradorLucke.Worker.LuckeOrchestratorWorker</c>).</param>
/// <param name="Message">Mensagem já formatada e truncada em <c>LogStreaming:MaxMessageLength</c>.</param>
/// <param name="Exception">
/// Exceção associada em texto (<c>ToString()</c>), quando houver — o painel mostra o rastro completo,
/// que é justamente o que o journal descarta em corpos multilinha.
/// </param>
public sealed record LogStreamEntry(
    DateTimeOffset TimestampUtc,
    LogLevel Level,
    string Category,
    string Message,
    string? Exception);
