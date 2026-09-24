using System.Threading.Channels;

namespace OrquestradorLucke.Worker;

/// <summary>
/// Canal de gatilho da indexação do RAG (Singleton). Funciona como um <em>debounce</em>: a
/// capacidade é 1 e o modo de saturação é <see cref="BoundedChannelFullMode.DropWrite"/>, portanto
/// uma rajada de webhooks deixa no máximo <b>um</b> pedido pendente — os seguintes são descartados
/// sem bloquear o produtor (o GitHub tem limite de tempo para receber a resposta).
/// </summary>
/// <remarks>
/// O endpoint do webhook é o lado produtor (apenas <see cref="TryWrite"/>, resposta 202 imediata) e
/// o <see cref="IndexingBackgroundService"/> é o único consumidor. O canal desacopla a latência da
/// resposta HTTP da duração da indexação e serializa o trabalho: o PostgreSQL recebe uma conexão
/// por vez, em vez de N indexações concorrentes disputando o índice vetorial.
/// </remarks>
public sealed class IndexingChannel
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });

    /// <summary>
    /// Publica um pedido de indexação.
    /// </summary>
    /// <param name="signal">Valor publicado; o canal carrega apenas a informação "há trabalho pendente".</param>
    /// <returns>
    /// <c>false</c> quando já existe um pedido pendente (a escrita foi descartada pelo
    /// <see cref="BoundedChannelFullMode.DropWrite"/>). O gatilho perdido é intencional: a indexação
    /// pendente já cobre a mudança que ele sinalizaria.
    /// </returns>
    public bool TryWrite(bool signal) => _channel.Writer.TryWrite(signal);

    /// <summary>Consome os pedidos de indexação até o canal ser completado ou o host ser encerrado.</summary>
    /// <param name="cancellationToken">Token de desligamento do host.</param>
    public IAsyncEnumerable<bool> ReadAllAsync(CancellationToken cancellationToken)
        => _channel.Reader.ReadAllAsync(cancellationToken);

    /// <summary>
    /// Completa a escrita do canal: novos <see cref="TryWrite"/> passam a devolver <c>false</c> e o
    /// consumidor encerra o <see cref="ReadAllAsync"/> ao drenar o que restou — evita que o laço de
    /// indexação fique esperando para sempre durante o desligamento do host.
    /// </summary>
    public void Complete() => _channel.Writer.TryComplete();
}
