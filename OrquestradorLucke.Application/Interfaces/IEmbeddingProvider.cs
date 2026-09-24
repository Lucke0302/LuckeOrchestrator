namespace OrquestradorLucke.Application.Interfaces;

/// <summary>
/// Abstração do modelo de embeddings que alimenta o índice vetorial do RAG. É um expert à parte das
/// cadeias do roteador MoE: não gera código, apenas converte texto em vetor.
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>Gera o vetor semântico do texto informado.</summary>
    /// <param name="text">Texto a embutir (documento indexado ou payload da tarefa).</param>
    /// <param name="cancellationToken">Token de cancelamento da iteração do worker.</param>
    /// <returns>
    /// Vetor produzido pelo modelo; <c>IsEmpty</c> quando o provedor não devolveu valores
    /// (resposta vazia ou fora do envelope esperado), caso em que o chamador deve tratar o
    /// documento como não indexado em vez de gravar um vetor degenerado.
    /// </returns>
    Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken);
}
