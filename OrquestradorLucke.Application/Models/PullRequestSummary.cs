namespace OrquestradorLucke.Application.Models;

/// <summary>
/// Resumo do pull request devolvido pelo expert de sumarização — este é o contrato JSON exigido no
/// prompt de
/// <see cref="Interfaces.ILLMProvider.GeneratePullRequestSummaryAsync(string, string, CancellationToken)"/>:
/// <c>{"titulo": "...", "descricao": "..."}</c>, sem cercas de markdown e sem texto fora do objeto.
/// </summary>
/// <remarks>
/// As propriedades são inicializadas com <see cref="string.Empty"/> em vez de nulas: chave ausente na
/// resposta (ou o JSON <c>{}</c>) cai no valor padrão e o chamador decide o fallback por
/// <see cref="string.IsNullOrWhiteSpace(string)"/> — nenhum <c>null</c> trafega pelo fluxo de entrega
/// do pull request.
/// </remarks>
public sealed record PullRequestSummary
{
    /// <summary>Título do pull request; vazio quando o modelo não o devolveu.</summary>
    public string Titulo { get; init; } = string.Empty;

    /// <summary>Corpo do pull request em Markdown; vazio quando o modelo não o devolveu.</summary>
    public string Descricao { get; init; } = string.Empty;
}
