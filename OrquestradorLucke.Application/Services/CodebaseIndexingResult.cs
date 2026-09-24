namespace OrquestradorLucke.Application.Services;

/// <summary>
/// Resumo de um ciclo de indexação da base de código (RAG).
/// </summary>
/// <param name="DiscoveredFiles">Arquivos <c>.cs</c> lidos da branch base do repositório.</param>
/// <param name="IndexedDocuments">Documentos gravados no índice (arquivos novos ou modificados).</param>
/// <param name="SkippedDocuments">Documentos descartados por não produzirem embedding válido.</param>
public sealed record CodebaseIndexingResult(int DiscoveredFiles, int IndexedDocuments, int SkippedDocuments);
