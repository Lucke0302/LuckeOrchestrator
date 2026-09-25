using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace OrquestradorLucke.Worker.Logging;

/// <summary>
/// Hub de streaming de logs (<c>/hubs/logs</c>): entrega ao painel web, em tempo real, o log da
/// aplicação — sem <c>journalctl</c>.
/// </summary>
/// <remarks>
/// <para>
/// Contrato do fio documentado em <see cref="LogStreamContract"/>: o cliente recebe o retrovisor no
/// evento <c>history</c> ao conectar e passa a acompanhar os eventos <c>log</c>. Não há método de
/// entrada: o painel é somente leitura (nenhum cliente publica log no daemon).
/// </para>
/// <para>
/// O hub é resolvido por conexão (o SignalR cria uma instância por invocação), portanto a injeção do
/// sink Singleton é segura — nada de estado de conexão guardado em campo.
/// </para>
/// <para>
/// <c>[Authorize]</c> na classe fecha o canal para anônimos: o log da aplicação expõe payload de
/// tarefa e caminho de arquivo. Como o WebSocket não envia o cabeçalho <c>Authorization</c>, o token
/// chega pela query string <c>access_token</c> (tratada no <c>OnMessageReceived</c> do JwtBearer, em
/// <c>AddJwtAuthentication</c>) e vale somente para esta rota.
/// </para>
/// </remarks>
[Authorize]
public sealed class LogHub(SignalRLogSink sink, ILogger<LogHub> logger) : Hub
{
    /// <inheritdoc />
    public override async Task OnConnectedAsync()
    {
        var history = sink.GetHistory();

        if (history.Count > 0)
        {
            await Clients.Caller
                .SendAsync(LogStreamContract.HistoryEventName, history)
                .ConfigureAwait(false);
        }

        logger.LogDebug(
            "Cliente {ConnectionId} conectado ao hub de logs ({HistoryCount} evento(s) de retrovisor).",
            Context.ConnectionId,
            history.Count);

        await base.OnConnectedAsync().ConfigureAwait(false);
    }
}
