using System.Threading.Channels;
using Microsoft.Extensions.Options;
using OrquestradorLucke.Worker.Configuration;

namespace OrquestradorLucke.Worker.Logging;

/// <summary>
/// Ponte entre o pipeline de <c>ILogger</c> e o hub de logs (Singleton). O provider de log escreve
/// aqui e o <see cref="LogBroadcastService"/> lê e publica nos clientes conectados.
/// </summary>
/// <remarks>
/// <para>
/// A fila é um <see cref="Channel{T}"/> limitado com <see cref="BoundedChannelFullMode.DropOldest"/>:
/// quem loga nunca espera por um cliente de SignalR. Sem o limite, um painel lento (ou travado em uma
/// aba do navegador) faria a fila crescer sem teto dentro do daemon — exatamente o vazamento de
/// memória que a arquitetura evita. Com ele, o custo do atraso é perder eventos antigos de log.
/// </para>
/// <para>
/// O retrovisor (<c>LogStreaming:HistorySize</c>) é um buffer circular protegido por lock: o cliente
/// que conecta depois do daemon enxerga o contexto recente em vez de uma tela vazia até o próximo
/// evento.
/// </para>
/// </remarks>
public sealed class SignalRLogSink
{
    /// <summary>Sufixo aplicado à mensagem truncada, sinalizando que o texto publicado é parcial.</summary>
    private const string TruncationSuffix = "...";

    private readonly Channel<LogStreamEntry> _channel;
    private readonly Queue<LogStreamEntry> _history = new();
    private readonly object _historyGate = new();
    private readonly LogStreamingOptions _options;

    /// <summary>
    /// Marca (por fluxo assíncrono) que o evento corrente está sendo publicado no hub. O
    /// <see cref="LogBroadcastService"/> liga a chave enquanto envia: se o próprio SignalR logar uma
    /// falha de envio, o provider descarta a entrada em vez de alimentar a fila que a originou — sem
    /// isso um envio que falha gera log, que gera envio, em laço infinito.
    /// </summary>
    private readonly AsyncLocal<bool> _broadcasting = new();

    public SignalRLogSink(IOptions<LogStreamingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;

        _channel = Channel.CreateBounded<LogStreamEntry>(
            new BoundedChannelOptions(Math.Max(1, _options.QueueCapacity))
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
    }

    /// <summary>Indica que o streaming está habilitado na configuração.</summary>
    public bool Enabled => _options.Enabled;

    /// <summary>Indica que o fluxo assíncrono corrente está publicando um evento no hub.</summary>
    public bool IsBroadcasting => _broadcasting.Value;

    /// <summary>Nível mínimo publicado no hub.</summary>
    public LogLevel MinimumLevel => _options.MinimumLevel;

    /// <summary>
    /// Publica um evento na fila e no retrovisor. Nunca bloqueia e nunca lança: log não pode derrubar
    /// o fluxo de negócio que o emitiu.
    /// </summary>
    /// <param name="entry">Evento já recortado pelo provider.</param>
    public void Publish(LogStreamEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!Enabled || IsBroadcasting)
        {
            return;
        }

        _channel.Writer.TryWrite(entry);

        if (_options.HistorySize > 0)
        {
            AppendToHistory(entry);
        }
    }

    /// <summary>Recorta os textos do evento no que o canal aceita publicar.</summary>
    /// <param name="timestampUtc">Instante do evento.</param>
    /// <param name="level">Nível do log.</param>
    /// <param name="category">Categoria do logger.</param>
    /// <param name="message">Mensagem já formatada.</param>
    /// <param name="exception">Exceção associada, quando houver.</param>
    public LogStreamEntry CreateEntry(
        DateTimeOffset timestampUtc,
        LogLevel level,
        string category,
        string message,
        Exception? exception)
        => new(
            timestampUtc,
            level,
            category,
            Truncate(message),
            exception?.ToString());

    /// <summary>
    /// Marca o fluxo corrente como "publicando": o provider descarta qualquer log emitido durante o
    /// envio (a proteção contra a realimentação do canal). O <c>Dispose</c> devolve o estado anterior.
    /// </summary>
    public IDisposable SuppressBroadcast()
    {
        var previous = _broadcasting.Value;

        _broadcasting.Value = true;

        return new SuppressionScope(_broadcasting, previous);
    }

    /// <summary>Consome os eventos publicados até o canal ser completado ou o host ser encerrado.</summary>
    /// <param name="cancellationToken">Token de desligamento do host.</param>
    public IAsyncEnumerable<LogStreamEntry> ReadAllAsync(CancellationToken cancellationToken)
        => _channel.Reader.ReadAllAsync(cancellationToken);

    /// <summary>Cópia do retrovisor (lista vazia quando o histórico está desligado).</summary>
    public IReadOnlyList<LogStreamEntry> GetHistory()
    {
        lock (_historyGate)
        {
            return _history.ToArray();
        }
    }

    /// <summary>
    /// Completa a escrita: novas publicações passam a ser descartadas e o consumidor encerra o
    /// <see cref="ReadAllAsync"/> ao drenar o que restou (usado no desligamento do host).
    /// </summary>
    public void Complete() => _channel.Writer.TryComplete();

    /// <summary>Guarda o evento no buffer circular, descartando o mais antigo quando o limite é atingido.</summary>
    private void AppendToHistory(LogStreamEntry entry)
    {
        var historySize = Math.Max(1, _options.HistorySize);

        lock (_historyGate)
        {
            while (_history.Count >= historySize)
            {
                _history.Dequeue();
            }

            _history.Enqueue(entry);
        }
    }

    /// <summary>Trunca a mensagem no teto configurado (o hub não é destino de despejo de payload).</summary>
    private string Truncate(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        var maxLength = Math.Max(1, _options.MaxMessageLength);

        return message.Length <= maxLength
            ? message
            : string.Concat(message.AsSpan(0, maxLength), TruncationSuffix);
    }

    /// <summary>Devolve a marca de "publicando" ao estado anterior ao fim do envio.</summary>
    private sealed class SuppressionScope(AsyncLocal<bool> flag, bool previous) : IDisposable
    {
        public void Dispose() => flag.Value = previous;
    }
}
