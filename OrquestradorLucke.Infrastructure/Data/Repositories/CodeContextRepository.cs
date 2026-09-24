using Microsoft.EntityFrameworkCore;
using OrquestradorLucke.Application.Interfaces;
using OrquestradorLucke.Domain;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace OrquestradorLucke.Infrastructure.Data.Repositories;

/// <summary>
/// Índice vetorial do RAG em PostgreSQL com pgvector. O upsert é incremental: compara o
/// <see cref="CodeDocument.ContentHash"/> gravado com o recebido e só escreve quando o conteúdo
/// mudou; a busca usa o operador de distância de cosseno (<c>&lt;=&gt;</c>) do pgvector.
/// </summary>
public sealed class CodeContextRepository(AppDbContext context) : ICodeContextRepository
{
    /// <inheritdoc />
    public async Task UpsertDocumentAsync(CodeDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        // Leitura sem rastreamento: apenas o necessário para decidir entre INSERT, UPDATE e no-op —
        // assim o contexto não fica com uma instância anexada que impediria o Update logo abaixo.
        var indexed = await context.CodeDocuments
            .AsNoTracking()
            .Where(candidate => candidate.FilePath == document.FilePath)
            .Select(candidate => new { candidate.Id, candidate.ContentHash })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        CodeDocument persisted;

        if (indexed is null)
        {
            persisted = document;
            context.CodeDocuments.Add(persisted);
        }
        else if (!string.Equals(indexed.ContentHash, document.ContentHash, StringComparison.Ordinal))
        {
            // Conteúdo alterado: mantém o Id do documento indexado e regrava conteúdo/hash/embedding.
            persisted = document with { Id = indexed.Id };
            context.CodeDocuments.Update(persisted);
        }
        else
        {
            // Mesmo hash: o vetor indexado continua válido, nada é escrito.
            return;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // O AppDbContext é compartilhado por todas as operações do escopo da iteração: a instância
        // gravada é destacada para que o próximo upsert do mesmo arquivo (portanto do mesmo Id) não
        // esbarre em uma cópia ainda rastreada no contexto.
        context.Entry(persisted).State = EntityState.Detached;
    }

    /// <inheritdoc />
    public async Task<List<CodeDocument>> SearchSimilarAsync(
        ReadOnlyMemory<float> queryEmbedding,
        int limit,
        CancellationToken cancellationToken)
    {
        // Um vetor vazio não é um ponto válido no espaço vetorial (o pgvector exige ao menos uma
        // dimensão): sem consulta, não há documentos a devolver.
        if (queryEmbedding.IsEmpty)
        {
            return [];
        }

        // 'embedding <=> $1' ordenado ascendentemente: menor distância de cosseno = mais próximo.
        // O provider precisa do vetor no tipo nativo (Pgvector.Vector) para o parâmetro ser um
        // 'vector' e não um array de reais.
        var queryVector = new Vector(queryEmbedding.ToArray());

        return await context.CodeDocuments
            .AsNoTracking()
            .OrderBy(document => document.Embedding.CosineDistance(queryVector))
            .Take(Math.Max(1, limit))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<List<string>> GetAllTrackedFilesAsync(CancellationToken cancellationToken)
        => await context.CodeDocuments
            .AsNoTracking()
            .Select(document => document.FilePath)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<Dictionary<string, string>> GetTrackedContentHashesAsync(CancellationToken cancellationToken)
    {
        var tracked = await context.CodeDocuments
            .AsNoTracking()
            .Select(document => new { document.FilePath, document.ContentHash })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var hashes = new Dictionary<string, string>(tracked.Count, StringComparer.Ordinal);

        foreach (var document in tracked)
        {
            hashes[document.FilePath] = document.ContentHash;
        }

        return hashes;
    }

    /// <inheritdoc />
    public async Task DeleteOrphanDocumentsAsync(IEnumerable<string> activeFilePaths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activeFilePaths);

        // Array + Distinct: o EF Core traduz o Contains sobre uma coleção em memória para
        // 'file_path = ANY(@p)' no Npgsql, e a lista sem duplicatas mantém o parâmetro pequeno
        // (a árvore C# de um repositório tem centenas de arquivos).
        var activePaths = activeFilePaths.Distinct(StringComparer.Ordinal).ToArray();

        // ExecuteDelete: o DELETE é emitido direto no banco, sem materializar os órfãos (o conteúdo
        // e o embedding de cada um são pesados) e sem passar pelo change tracker.
        await context.CodeDocuments
            .Where(document => !activePaths.Contains(document.FilePath))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
