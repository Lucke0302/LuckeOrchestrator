using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OrquestradorLucke.Worker.Logging;

/// <summary>
/// Consumidor da fila de logs (<see cref="SignalRLogSink"/>) e publicador no <see cref="LogHub"/>:
/// transforma cada evento capturado pelo <see cref="SignalRLoggerProvider"/> em uma mensagem SignalR
/// para todos os clientes conectados.
/// </summary>
/// <remarks>
/// <para>
/// Fica fora do caminho de quem loga: a escrita no sink é uma operação de canal (sem I/O) e o envio
/// acontece aqui, em um BackgroundService. Um cliente lento ou desconectado não bloqueia o laço do
/// orquestrador.
/// </para>
/// <para>
/// Todo envio roda com <see cref="SignalRLogSink.SuppressBroadcast"/> ligado: se o SignalR logar
/// algum erro de transporte durante o envio, esse log não volta para a fila que o originou (a
/// proteção contra realimentação). Falhas de envio são absorvidas de propósito — e não logadas —
/// porque registrar a falha aqui realimentaria o canal indefinidamente.
/// </para>
/// </remarks>
public sealed class LogBroadcastService(
    SignalRLogSink sink,
    IHubContext<LogHub> hubContext,
    ILogger<LogBroadcastService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Streaming de logs habilitado: os clientes do hub '{HubRoute}' recebem o log do daemon em tempo real.",
            LogStreamContract.HubRoute);

        try
        {
            await foreach (var entry in sink.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                using var suppression = sink.SuppressBroadcast();

                try
                {
                    await hubContext.Clients.All
                        .SendAsync(LogStreamContract.LogEventName, entry, stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    // Absorvido: logar aqui alimentaria a fila que acabou de falhar em publicar.
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Desligamento do host: o log restante não é essencial.
        }
        finally
        {
            // Nenhum consumidor assume a fila: completa a escrita para que uma leitura futura
            // encerre em vez de aguardar indefinidamente.
            sink.Complete();
        }
    }
}
