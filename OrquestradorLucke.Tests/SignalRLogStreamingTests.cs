using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrquestradorLucke.Worker.Configuration;
using OrquestradorLucke.Worker.Logging;

namespace OrquestradorLucke.Tests;

/// <summary>
/// Streaming de logs: o provider intercepta o pipeline de <c>ILogger</c> e entrega os eventos ao canal
/// que o <c>LogBroadcastService</c> publica no hub — com filtro de nível, truncamento, retrovisor para
/// quem conecta depois e a proteção contra realimentação (o log emitido durante a publicação não volta
/// para a fila, senão um envio que falha viraria um laço infinito).
/// </summary>
public sealed class SignalRLogStreamingTests
{
    private const string Category = "OrquestradorLucke.Tests.LogStream";

    /// <summary>Tempo curto o bastante para provar que NADA foi publicado sem atrasar a suíte.</summary>
    private static readonly TimeSpan AbsenceTimeout = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task Logger_DevePublicarNivelCategoriaMensagemFormatadaEExcecao()
    {
        var sink = CreateSink();
        var logger = CreateLogger(sink);
        var taskId = Guid.NewGuid();
        var exception = new InvalidOperationException("falha simulada na entrega");

        logger.LogError(exception, "Entrega da tarefa {TaskId} falhou.", taskId);

        var entry = await ReadNextAsync(sink);

        entry.Should().NotBeNull();
        entry!.Level.Should().Be(LogLevel.Error);
        entry.Category.Should().Be(Category);

        // Mensagem formatada com os argumentos: o painel não recebe o template cru.
        entry.Message.Should().Be($"Entrega da tarefa {taskId} falhou.");
        entry.Exception.Should().Contain("falha simulada na entrega");
        entry.TimestampUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Logger_AbaixoDoNivelMinimo_NaoDevePublicar()
    {
        var sink = CreateSink(minimumLevel: LogLevel.Warning);
        var logger = CreateLogger(sink);

        logger.IsEnabled(LogLevel.Information).Should().BeFalse();

        logger.LogInformation("detalhe que o painel não pediu");

        (await ReadNextAsync(sink, AbsenceTimeout)).Should().BeNull();

        logger.LogWarning("aviso relevante");

        (await ReadNextAsync(sink))!.Message.Should().Be("aviso relevante");
    }

    [Fact]
    public async Task Logger_MensagemAcimaDoTeto_DeveSerTruncada()
    {
        var sink = CreateSink(maxMessageLength: 10);
        var logger = CreateLogger(sink);

        logger.LogInformation("0123456789ABCDEF");

        (await ReadNextAsync(sink))!.Message.Should().Be("0123456789...");
    }

    [Fact]
    public async Task Sink_DeveManterApenasOsEventosMaisRecentesNoRetrovisor()
    {
        var sink = CreateSink(historySize: 2);
        var logger = CreateLogger(sink);

        logger.LogInformation("primeiro");
        logger.LogInformation("segundo");
        logger.LogInformation("terceiro");

        var history = sink.GetHistory();

        history.Should().HaveCount(2);
        history.Select(entry => entry.Message).Should().Equal("segundo", "terceiro");
    }

    [Fact]
    public async Task Sink_DuranteAPublicacao_DeveDescartarOQueOProprioEnvioLogar()
    {
        var sink = CreateSink();
        var logger = CreateLogger(sink);

        using (sink.SuppressBroadcast())
        {
            sink.IsBroadcasting.Should().BeTrue();
            logger.LogInformation("log emitido pelo próprio envio ao hub");
        }

        sink.IsBroadcasting.Should().BeFalse();

        (await ReadNextAsync(sink, AbsenceTimeout)).Should().BeNull();
        sink.GetHistory().Should().BeEmpty();
    }

    [Fact]
    public async Task Sink_Desabilitado_NaoDevePublicarNemAlimentarORetrovisor()
    {
        var sink = CreateSink(enabled: false);
        var logger = CreateLogger(sink);

        logger.IsEnabled(LogLevel.Information).Should().BeFalse();

        logger.LogInformation("nada deve sair");

        (await ReadNextAsync(sink, AbsenceTimeout)).Should().BeNull();
        sink.GetHistory().Should().BeEmpty();
    }

    [Fact]
    public async Task Provider_RegistradoNaDI_DeveEntrarNoPipelineDeLoggingDoHost()
    {
        // O host não instancia o provider: o LoggerFactory resolvido do container pega TODOS os
        // ILoggerProvider registrados na DI — é assim que o streaming intercepta o log da aplicação.
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton<SignalRLogSink>();
        services.AddSingleton<ILoggerProvider, SignalRLoggerProvider>();
        services.Configure<LogStreamingOptions>(options =>
        {
            options.Enabled = true;
            options.MinimumLevel = LogLevel.Information;
            options.HistorySize = 5;
            options.QueueCapacity = 16;
        });

        using var provider = services.BuildServiceProvider();

        var sink = provider.GetRequiredService<SignalRLogSink>();
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();

        loggerFactory.CreateLogger(Category).LogInformation("evento do pipeline real");

        (await ReadNextAsync(sink))!.Message.Should().Be("evento do pipeline real");
        sink.GetHistory().Should().ContainSingle();
    }

    [Fact]
    public async Task Complete_DeveEncerrarALeituraDoCanal()
    {
        var sink = CreateSink();

        sink.Complete();

        var readToEnd = async () =>
        {
            await foreach (var _ in sink.ReadAllAsync(CancellationToken.None))
            {
                // drena o que restou
            }
        };

        await readToEnd.Should().NotThrowAsync();
    }

    /// <summary>
    /// Provider sob teste: é o ponto de entrada real do pipeline de log (o logger é criado por ele, e a
    /// categoria vem do nome usado na criação).
    /// </summary>
    private static ILogger CreateLogger(SignalRLogSink sink) => new SignalRLoggerProvider(sink).CreateLogger(Category);

    private static SignalRLogSink CreateSink(
        bool enabled = true,
        LogLevel minimumLevel = LogLevel.Information,
        int historySize = 200,
        int maxMessageLength = 4000)
        => new(Options.Create(new LogStreamingOptions
        {
            Enabled = enabled,
            MinimumLevel = minimumLevel,
            HistorySize = historySize,
            MaxMessageLength = maxMessageLength,
            QueueCapacity = 16
        }));

    /// <summary>
    /// Lê o próximo evento publicado; devolve <c>null</c> quando o timeout expira sem nenhum evento (é
    /// assim que os testes provam a ausência de publicação).
    /// </summary>
    private static async Task<LogStreamEntry?> ReadNextAsync(SignalRLogSink sink, TimeSpan? timeout = null)
    {
        using var timeoutSource = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));

        try
        {
            await foreach (var entry in sink.ReadAllAsync(timeoutSource.Token))
            {
                return entry;
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout: o canal não recebeu nada no intervalo.
        }

        return null;
    }
}
