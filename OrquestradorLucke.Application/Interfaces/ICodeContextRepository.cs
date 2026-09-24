using OrquestradorLucke.Domain;

namespace OrquestradorLucke.Application.Interfaces;

/// <summary>
/// Índice vetorial da base de código (RAG). A implementação real usa PostgreSQL + pgvector na
/// Infrastructure; a Application conhece apenas o contrato.
/// </summary>
public interface ICodeContextRepository
{
    /// <summary>
    /// Insere o documento ou atualiza a versão existente do mesmo <see cref="CodeDocument.FilePath"/>.
    /// Se o <see cref="CodeDocument.ContentHash"/> já corresponder ao gravado, nada é escrito.
    /// </summary>
    /// <param name="document">Documento indexado (caminho, hash, conteúdo e embedding).</param>
    /// <param name="cancellationToken">Token de cancelamento da iteração do worker.</param>
    Task UpsertDocumentAsync(CodeDocument document, CancellationToken cancellationToken);

    /// <summary>Recupera os <paramref name="limit"/> documentos mais próximos do vetor informado.</summary>
    /// <param name="queryEmbedding">Vetor de consulta (payload da tarefa embutido).</param>
    /// <param name="limit">Quantidade máxima de documentos devolvidos.</param>
    /// <param name="cancellationToken">Token de cancelamento da iteração do worker.</param>
    /// <returns>Documentos ordenados por proximidade (menor distância de cosseno primeiro).</returns>
    Task<List<CodeDocument>> SearchSimilarAsync(ReadOnlyMemory<float> queryEmbedding, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Lista os caminhos já presentes no índice — usado pelo indexador para identificar arquivos novos.
    /// </summary>
    /// <param name="cancellationToken">Token de cancelamento da iteração do worker.</param>
    Task<List<string>> GetAllTrackedFilesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Devolve os hashes gravados por caminho (<see cref="CodeDocument.FilePath"/> →
    /// <see cref="CodeDocument.ContentHash"/>). Sem eles não é possível detectar arquivos
    /// modificados sem reembedar o arquivo inteiro a cada ciclo do indexador.
    /// </summary>
    /// <param name="cancellationToken">Token de cancelamento da iteração do worker.</param>
    Task<Dictionary<string, string>> GetTrackedContentHashesAsync(CancellationToken cancellationToken);
}
