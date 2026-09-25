using Microsoft.Extensions.Logging;

namespace OrquestradorLucke.Worker.Configuration;

/// <summary>
/// Opções do streaming de logs pelo SignalR (o painel web consome o log do daemon em tempo real, sem
/// <c>journalctl</c>).
/// </summary>
public sealed class LogStreamingOptions
{
    public const string SectionName = "LogStreaming";

    /// <summary>Habilita/desabilita o provider. Desligado, nenhum evento entra na fila do hub.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Nível mínimo publicado no hub. É independente do filtro do console/journald
    /// (<c>Logging:LogLevel</c>): o painel pode receber mais (ou menos) detalhe que o log do serviço,
    /// e um <c>Debug</c> aqui não obriga o journal a guardar tudo.
    /// </summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

    /// <summary>
    /// Capacidade da fila entre o provider (produtor) e o BackgroundService que publica no hub
    /// (consumidor). Cheia, o evento mais antigo é descartado: a aplicação nunca bloqueia por causa do
    /// log, e um cliente lento não vira memória crescente no daemon.
    /// </summary>
    public int QueueCapacity { get; set; } = 1000;

    /// <summary>
    /// Quantidade de eventos mantidos em memória para o cliente que conecta depois: ao abrir o hub ele
    /// recebe esse retrovisor (evento <c>history</c>) antes de acompanhar o fluxo. Zero desliga o
    /// histórico.
    /// </summary>
    public int HistorySize { get; set; } = 200;

    /// <summary>
    /// Teto de caracteres da mensagem publicada. Uma mensagem maior é truncada com <c>...</c> — o hub
    /// não é o destino de despejo de payload (o corpo multilinha gigante já é achatado no log).
    /// </summary>
    public int MaxMessageLength { get; set; } = 4000;
}
