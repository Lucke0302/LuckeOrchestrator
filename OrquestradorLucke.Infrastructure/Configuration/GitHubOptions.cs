namespace OrquestradorLucke.Infrastructure.Configuration;

/// <summary>
/// Opções de acesso ao GitHub pela conta de agente autônomo. O token deve vir de
/// user-secrets ou variável de ambiente — nunca hardcoded no código.
/// </summary>
public sealed class GitHubOptions
{
    public const string SectionName = "GitHub";

    /// <summary>Token do agente autônomo.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Dono do repositório (usuário ou organização).</summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>Nome do repositório de trabalho.</summary>
    public string Repository { get; set; } = string.Empty;

    /// <summary>Branch base para criação das branches do agente e destino do pull request.</summary>
    public string BaseBranch { get; set; } = string.Empty;
}
