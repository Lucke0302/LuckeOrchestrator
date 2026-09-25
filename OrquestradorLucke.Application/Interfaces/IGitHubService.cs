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

    /// <summary>
    /// Mescla o pull request aberto cuja origem é a branch informada — a aprovação do revisor humano.
    /// </summary>
    /// <remarks>
    /// Operação de <b>revisão</b>: usa a identidade administrativa (token do revisor), nunca a conta do
    /// agente autônomo. A branch e o repositório são os mesmos das demais operações.
    /// </remarks>
    /// <param name="branchName">Branch de origem do pull request (ex.: <c>feat/task-{id}</c>).</param>
    /// <param name="commitTitle">Título do commit de merge criado pelo GitHub.</param>
    /// <param name="cancellationToken">Token de cancelamento da requisição HTTP.</param>
    /// <returns>SHA do commit de merge devolvido pelo GitHub.</returns>
    Task<string> MergePullRequestAsync(string branchName, string commitTitle, CancellationToken cancellationToken);

    /// <summary>
    /// Fecha (sem merge) o pull request aberto cuja origem é a branch informada, registrando o motivo
    /// da rejeição como comentário no PR.
    /// </summary>
    /// <remarks>
    /// Operação de <b>revisão</b>: usa a identidade administrativa (token do revisor). O fechamento é
    /// idempotente — branch sem pull request aberto não é erro (nada a fechar) e o fluxo do revisor
    /// segue para devolver a tarefa à fila.
    /// </remarks>
    /// <param name="branchName">Branch de origem do pull request (ex.: <c>feat/task-{id}</c>).</param>
    /// <param name="reason">Motivo da rejeição, publicado como comentário do pull request.</param>
    /// <param name="cancellationToken">Token de cancelamento da requisição HTTP.</param>
    Task ClosePullRequestAsync(string branchName, string reason, CancellationToken cancellationToken);

    /// <summary>
    /// Lê os arquivos C# versionados na branch base do repositório (Git Data API, sem clonar o
    /// repositório localmente). É a fonte de dados do índice vetorial do RAG.
    /// </summary>
    /// <param name="cancellationToken">Token de cancelamento da iteração do worker.</param>
    /// <returns>
    /// Dicionário caminho relativo → conteúdo textual dos arquivos <c>.cs</c>. Arquivos que a API
    /// devolve sem conteúdo (blobs grandes, codificação não textual) são omitidos.
    /// </returns>
    Task<Dictionary<string, string>> GetRepositoryCSharpFilesAsync(CancellationToken cancellationToken);
}
