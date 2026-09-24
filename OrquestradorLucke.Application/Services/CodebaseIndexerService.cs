using System.Security.Cryptography;
using System.Text;
using OrquestradorLucke.Application.Interfaces;
using OrquestradorLucke.Domain;

namespace OrquestradorLucke.Application.Services;

/// <summary>
/// Indexador incremental da base de código para o RAG. Compara os arquivos C# versionados na branch
/// base com o que já está no índice vetorial e embute (e grava) apenas o que é novo ou teve o
/// conteúdo alterado — embeddings consomem cota, portanto arquivos inalterados nunca são reenviados
/// ao provedor.
/// </summary>
/// <remarks>
/// Serviço Scoped: criado dentro do escopo de cada iteração do worker, junto com o repositório e o
/// provedor de embeddings que consome.
/// </remarks>
public sealed class CodebaseIndexerService(
    IGitHubService gitHubService,
    ICodeContextRepository codeContextRepository,
    IEmbeddingProvider embeddingProvider)
{
    /// <summary>Sincroniza o índice vetorial com a branch base do repositório de trabalho.</summary>
    /// <param name="cancellationToken">Token de cancelamento da iteração do worker.</param>
    /// <returns>Resumo do ciclo (arquivos descobertos, indexados e descartados).</returns>
    public async Task<CodebaseIndexingResult> IndexChangedDocumentsAsync(CancellationToken cancellationToken)
    {
        var files = await gitHubService
            .GetRepositoryCSharpFilesAsync(cancellationToken)
            .ConfigureAwait(false);

        if (files.Count == 0)
        {
            return new CodebaseIndexingResult(0, 0, 0);
        }

        // Caminhos já presentes no índice: identificam os arquivos novos.
        var trackedFiles = new HashSet<string>(
            await codeContextRepository.GetAllTrackedFilesAsync(cancellationToken).ConfigureAwait(false),
            StringComparer.Ordinal);

        // Hashes gravados: identificam os arquivos modificados sem embutir todo o repositório de novo.
        var trackedHashes = new Dictionary<string, string>(
            await codeContextRepository.GetTrackedContentHashesAsync(cancellationToken).ConfigureAwait(false),
            StringComparer.Ordinal);

        var indexed = 0;
        var skipped = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var contentHash = ComputeContentHash(file.Value);

            if (trackedFiles.Contains(file.Key) && IsUnchanged(trackedHashes, file.Key, contentHash))
            {
                continue;
            }

            var embedding = await embeddingProvider
                .GenerateEmbeddingAsync(file.Value, cancellationToken)
                .ConfigureAwait(false);

            if (embedding.IsEmpty)
            {
                // Sem vetor o documento não é recuperável por similaridade de cosseno: fica fora do
                // índice (nada de vetor degenerado na coluna vector) e o próximo ciclo tenta de novo.
                skipped++;
                continue;
            }

            await codeContextRepository
                .UpsertDocumentAsync(
                    new CodeDocument
                    {
                        FilePath = file.Key,
                        ContentHash = contentHash,
                        Content = file.Value,
                        Embedding = embedding
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            indexed++;
        }

        // Faxina do índice: o upsert só insere e atualiza, então um arquivo deletado (ou renomeado)
        // na branch base continuaria sendo recuperado como referência pelo RAG. As chaves do
        // dicionário lido do GitHub são a árvore atual — o que não está nelas virou documento órfão.
        await codeContextRepository
            .DeleteOrphanDocumentsAsync(files.Keys, cancellationToken)
            .ConfigureAwait(false);

        return new CodebaseIndexingResult(files.Count, indexed, skipped);
    }

    /// <summary>Indica que o arquivo já indexado mantém exatamente o mesmo hash de conteúdo.</summary>
    private static bool IsUnchanged(IDictionary<string, string> trackedHashes, string filePath, string contentHash)
        => trackedHashes.TryGetValue(filePath, out var storedHash)
            && string.Equals(storedHash, contentHash, StringComparison.Ordinal);

    /// <summary>Hash SHA-256 (hexadecimal) do conteúdo — chave do índice incremental.</summary>
    private static string ComputeContentHash(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
