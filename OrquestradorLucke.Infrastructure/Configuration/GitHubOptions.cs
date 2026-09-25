namespace OrquestradorLucke.Infrastructure.Configuration;

/// <summary>
/// Opções de acesso ao GitHub. Os dois tokens são <b>segredos</b> e devem vir de user-secrets ou
/// variável de ambiente — nunca hardcoded no código.
/// </summary>
/// <remarks>
/// A segregação de funções é feita por identidade: o agente autônomo entrega
/// (branch/commit/pull request) com <see cref="AgentToken"/>, enquanto a revisão humana
/// (merge/fechamento de pull request) usa <see cref="AdminToken"/>. Assim a conta do agente não tem
/// poder de aprovar a própria entrega.
/// </remarks>
public sealed class GitHubOptions
{
    public const string SectionName = "GitHub";

    /// <summary>Token do agente autônomo: branch, commit, pull request e leitura da árvore para o RAG.</summary>
    public string AgentToken { get; set; } = string.Empty;

    /// <summary>
    /// Token do revisor humano: mescla e fecha pull requests (<c>accept</c>/<c>reject</c>). A ausência
    /// dele não impede o daemon de entregar, mas qualquer operação de revisão falha de forma explícita
    /// (fail closed) em vez de reutilizar silenciosamente a credencial do agente.
    /// </summary>
    public string AdminToken { get; set; } = string.Empty;

    /// <summary>Dono do repositório (usuário ou organização).</summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>Nome do repositório de trabalho.</summary>
    public string Repository { get; set; } = string.Empty;

    /// <summary>Branch base para criação das branches do agente e destino do pull request.</summary>
    public string BaseBranch { get; set; } = string.Empty;

    /// <summary>
    /// Estratégia de merge aplicada na aprovação da revisão (<c>Merge</c>, <c>Squash</c> ou
    /// <c>Rebase</c>). Valor inválido cai em <c>Merge</c> com aviso no log.
    /// </summary>
    public string MergeMethod { get; set; } = "Merge";
}
