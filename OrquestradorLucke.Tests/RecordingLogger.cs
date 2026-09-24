using Microsoft.Extensions.Logging;

namespace OrquestradorLucke.Tests;

/// <summary>
/// Logger de teste que guarda o que foi logado: nível, mensagem formatada e exceção. Usado para
/// verificar a auditoria (erros de entrega, contagem de arquivos, prévia da resposta do LLM) sem
/// depender de infraestrutura de log nem do <c>IsEnabled</c> default de um mock de <see cref="ILogger{T}"/>.
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<RecordedLog> Entries { get; } = [];

    /// <summary>Mensagens formatadas, na ordem em que foram logadas.</summary>
    public IEnumerable<string> Messages => Entries.Select(entry => entry.Message);

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Entries.Add(new RecordedLog(logLevel, formatter(state, exception), exception));

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}

/// <summary>Evento de log capturado por <see cref="RecordingLogger{T}"/>.</summary>
/// <param name="Level">Nível do evento.</param>
/// <param name="Message">Mensagem já formatada.</param>
/// <param name="Exception">Exceção associada, quando houver.</param>
internal readonly record struct RecordedLog(LogLevel Level, string Message, Exception? Exception);
