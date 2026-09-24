using OrquestradorLucke.Application.Services;

namespace OrquestradorLucke.Worker;

/// <summary>
/// Consumidor do gatilho de indexação do RAG: lê o <see cref="IndexingChannel"/> com
/// <c>ReadAllAsync()</c> e executa o <see cref="CodebaseIndexerService"/> um pedido por vez.
/// </summary>
/// <remarks>
/// Substitui o "fire-and-forget" que o webhook disparava em <c>Program</c>: cada entrega abria um
/// escopo e rodava o indexador em paralelo. Aqui o escopo é criado com
/// <see cref="IServiceScopeFactory"/> (o indexador e o <c>DbContext</c> que ele consome são Scoped —
/// nunca injetados no construtor do <see cref="BackgroundService"/>) e é descartado ao fim de cada
/// pedido, garantindo que o PostgreSQL receba uma conexão por vez.
/// </remarks>
public sealed class IndexingBackgroundService(
    IndexingChannel indexingChannel,
    IServiceScopeFactory scopeFactory,
    ILogger<IndexingBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var _ in indexingChannel.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await SynchronizeCodebaseIndexAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Desligamento do host: a indexação é best-effort e o índice atual continua servindo de
            // contexto — o próximo ciclo (intervalo ou novo webhook) reindexa o que faltou.
        }
        finally
        {
            // Nenhum outro consumidor assume o canal: completa a escrita para que uma leitura
            // futura encerre em vez de aguardar indefinidamente.
            indexingChannel.Complete();
        }
    }

    /// <summary>
    /// Sincroniza o índice vetorial do RAG em um escopo próprio, descartado ao fim do pedido.
    /// </summary>
    /// <remarks>
    /// A falha é absorvida com log de propósito: o gatilho já foi aceito (HTTP 202) e a indexação é
    /// enriquecimento do RAG — derrubar o laço por causa dela interromperia os demais pedidos.
    /// </remarks>
    private async Task SynchronizeCodebaseIndexAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();

            var codeIndexer = scope.ServiceProvider.GetRequiredService<CodebaseIndexerService>();

            var result = await codeIndexer
                .IndexChangedDocumentsAsync(stoppingToken)
                .ConfigureAwait(false);

            logger.LogInformation(
                "Índice do RAG sincronizado pelo webhook: {Indexed} documento(s) novos/alterados entre {Discovered} arquivo(s) C#{Skipped}.",
                result.IndexedDocuments,
                result.DiscoveredFiles,
                result.SkippedDocuments > 0
                    ? $"; {result.SkippedDocuments} descartado(s) por não gerarem embedding"
                    : string.Empty);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao sincronizar o índice do RAG pelo webhook do GitHub.");
        }
    }
}
