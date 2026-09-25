using Microsoft.Extensions.Logging;

namespace OrquestradorLucke.Worker.Logging;

/// <summary>
/// Provider de <see cref="ILogger"/> que intercepta TUDO o que a aplicação loga e transmite em tempo
/// real para os clientes conectados no <see cref="LogHub"/>.
/// </summary>
/// <remarks>
/// <para>
/// Registrado como Singleton na DI (<c>AddSingleton&lt;ILoggerProvider, SignalRLoggerProvider&gt;</c>):
/// o <see cref="LoggerFactory"/> do host resolve todos os <see cref="ILoggerProvider"/> do container,
/// então o provider entra no pipeline ao lado do console/journald — nenhum provider existente é
/// substituído, e o mesmo evento continua indo para o systemd.
/// </para>
/// <para>
/// O provider <b>não conhece o SignalR</b>: ele apenas entrega o evento ao
/// <see cref="SignalRLogSink"/> (fila não bloqueante). Quem envia é o
/// <see cref="LogBroadcastService"/>, um BackgroundService — logging nunca espera por I/O de rede, e
/// uma falha no hub não afeta a aplicação.
/// </para>
/// <para>
/// O alias <c>LogStream</c> permite filtrar por provider no appsettings
/// (<c>Logging:LogLevel:LogStream</c>), além do <c>LogStreaming:MinimumLevel</c> que controla só o
/// que vai para o painel.
/// </para>
/// </remarks>
[ProviderAlias(ProviderAliasName)]
public sealed class SignalRLoggerProvider(SignalRLogSink sink) : ILoggerProvider
{
    /// <summary>Alias do provider na configuração de níveis (<c>Logging:LogLevel:LogStream</c>).</summary>
    public const string ProviderAliasName = "LogStream";

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new SignalRLogger(categoryName, sink);

    /// <inheritdoc />
    public void Dispose()
    {
        // Nada a descartar: o canal e o buffer circular pertencem ao sink (Singleton do host).
    }

    /// <summary>
    /// Logger que traduz cada chamada em um <see cref="LogStreamEntry"/> publicado no sink.
    /// </summary>
    /// <param name="categoryName">Categoria do logger (vira o campo <c>category</c> do evento).</param>
    /// <param name="sink">Ponte com o hub de logs.</param>
    private sealed class SignalRLogger(string categoryName, SignalRLogSink sink) : ILogger
    {
        /// <inheritdoc />
        /// <remarks>Sem escopos: o painel mostra a mensagem formatada, não a hierarquia de escopos.</remarks>
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel)
            => sink.Enabled && logLevel != LogLevel.None && logLevel >= sink.MinimumLevel;

        /// <inheritdoc />
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || sink.IsBroadcasting)
            {
                return;
            }

            ArgumentNullException.ThrowIfNull(formatter);

            var message = formatter(state, exception);

            if (string.IsNullOrEmpty(message) && exception is null)
            {
                return;
            }

            sink.Publish(sink.CreateEntry(DateTimeOffset.UtcNow, logLevel, categoryName, message, exception));
        }
    }
}
