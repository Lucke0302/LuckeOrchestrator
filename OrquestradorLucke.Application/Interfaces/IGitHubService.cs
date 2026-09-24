namespace OrquestradorLucke.Application.Interfaces;

/// <summary>
/// Operações de Git/GitHub executadas por uma conta de agente autônomo
/// (branch, commit e pull request), isoladas em um serviço dedicado.
/// </summary>
public interface IGitHubService
{
    /// <summary>Cria a branch de trabalho a partir da branch padrão do repositório.</summary>
    /// <returns>Nome completo da referência criada (ex.: <c>refs/heads/agent/lucke-123</c>).</returns>
    Task<string> CreateBranchAsync(string branchName, CancellationToken cancellationToken);

    /// <summary>
    /// Aplica as alterações de arquivos na branch informada e cria o commit.
    /// </summary>
    /// <param name="fileContents">
    /// Alterações a persistir: a chave é o caminho relativo do arquivo no repositório
    /// e o valor é o conteúdo final do arquivo.
    /// </param>
    /// <returns>SHA do commit criado.</returns>
    Task<string> CommitChangesAsync(string branchName, string commitMessage, IReadOnlyDictionary<string, string> fileContents, CancellationToken cancellationToken);

    /// <summary>Abre o pull request da branch informada.</summary>
    /// <returns>URL absoluta do pull request aberto.</returns>
    Task<string> OpenPullRequestAsync(string branchName, string title, string description, CancellationToken cancellationToken);
}
