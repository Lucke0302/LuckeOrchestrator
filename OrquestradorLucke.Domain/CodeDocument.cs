namespace OrquestradorLucke.Domain;

/// <summary>
/// Documento de código-fonte indexado para o RAG (Retrieval-Augmented Generation): é a unidade
/// consultada por similaridade semântica para dar ao expert contexto da própria base de código.
/// </summary>
/// <remarks>
/// O <see cref="Embedding"/> é um vetor de <see cref="float"/> mantido como
/// <see cref="ReadOnlyMemory{T}"/> para não carregar o payload vetorial em comparações de igualdade
/// do record. O mapeamento para a coluna <c>vector</c> do pgvector é responsabilidade da
/// Infrastructure — o Domain não conhece o provedor de banco.
/// </remarks>
public record CodeDocument
{
    /// <summary>Identificador único do documento.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Caminho relativo do arquivo dentro do repositório (chave natural do índice).</summary>
    public required string FilePath { get; init; }

    /// <summary>Hash do conteúdo (SHA-256 hexadecimal) usado pelo índice incremental.</summary>
    public required string ContentHash { get; init; }

    /// <summary>Conteúdo textual do arquivo, enviado ao expert como referência.</summary>
    public required string Content { get; init; }

    /// <summary>Vetor semântico do conteúdo; vazio quando o arquivo ainda não foi embutido.</summary>
    public ReadOnlyMemory<float> Embedding { get; init; }
}
