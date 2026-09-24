using Microsoft.Extensions.Options;
using Octokit;
using OrquestradorLucke.Application.Interfaces;
using OrquestradorLucke.Infrastructure.Configuration;

namespace OrquestradorLucke.Infrastructure.Adapters;

/// <summary>
/// Adapter do GitHub (conta de agente autônomo). Executa branch, commit e pull request pela
/// Git Data API do Octokit com o repositório puramente em memória: blobs, árvore e commit são
/// montados como objetos e trafegam por HTTP, portanto nada é clonado, lido ou escrito no
/// sistema de arquivos local.
/// </summary>
public sealed class GitHubAdapter : IGitHubService
{
    /// <summary>Identificador do produto enviado no cabeçalho de agente das requisições ao GitHub.</summary>
    private const string ProductHeaderName = "LuckeOrchestrator";

    /// <summary>Prefixo da referência completa de uma branch no repositório.</summary>
    private const string BranchRefPrefix = "refs/heads/";

    /// <summary>Prefixo aceito pela API de referências (sem o segmento <c>refs/</c>).</summary>
    private const string HeadRefPrefix = "heads/";

    /// <summary>Modo de arquivo regular na árvore do Git ("100644").</summary>
    private const string FileMode = "100644";

    private readonly GitHubOptions _options;

    /// <summary>
    /// Cria o adapter e autentica o cliente Octokit. O token vem de <see cref="GitHubOptions"/>
    /// (user-secrets ou variável de ambiente) — nunca hardcoded.
    /// </summary>
    /// <exception cref="InvalidOperationException">Quando <see cref="GitHubOptions.Token"/> não está configurado.</exception>
    public GitHubAdapter(IOptions<GitHubOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;

        // Fail fast: sem token não há operação possível, e Credentials lançaria algo menos claro na primeira chamada.
        if (string.IsNullOrWhiteSpace(_options.Token))
        {
            throw new InvalidOperationException(
                $"{nameof(GitHubOptions)}.{nameof(GitHubOptions.Token)} não configurado. Defina via user-secrets ou variável de ambiente.");
        }

        Client = new GitHubClient(new ProductHeaderValue(ProductHeaderName));
        Client.Credentials = new Credentials(_options.Token);
    }

    /// <summary>Cliente Octokit autenticado, usado pelas operações de Git e pull request.</summary>
    internal GitHubClient Client { get; }

    /// <inheritdoc />
    /// <remarks>
    /// A versão 14 do Octokit não expõe overloads com <see cref="CancellationToken"/> nas rotas de
    /// Git/pull request; por isso o token é validado no início da operação.
    /// </remarks>
    public async Task<string> CreateBranchAsync(string branchName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        EnsureRepositoryConfiguration();
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);

        // Ponto de partida: o commit mais recente da branch base configurada (GitHubOptions.BaseBranch).
        var baseReference = await Client.Git.Reference
            .Get(_options.Owner, _options.Repository, HeadRefPrefix + _options.BaseBranch)
            .ConfigureAwait(false);

        var newReference = new NewReference(BranchRefPrefix + branchName, baseReference.Object.Sha);

        var createdReference = await Client.Git.Reference
            .Create(_options.Owner, _options.Repository, newReference)
            .ConfigureAwait(false);

        return createdReference.Ref;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A versão 14 do Octokit não expõe overloads com <see cref="CancellationToken"/> nas rotas de
    /// Git/pull request; por isso o token é validado no início da operação e a cada arquivo.
    /// </remarks>
    public async Task<string> CommitChangesAsync(string branchName, string commitMessage, IReadOnlyDictionary<string, string> fileContents, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        EnsureRepositoryConfiguration();
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        ArgumentException.ThrowIfNullOrWhiteSpace(commitMessage);
        ArgumentNullException.ThrowIfNull(fileContents);

        var owner = _options.Owner;
        var repository = _options.Repository;
        var headReference = HeadRefPrefix + branchName;

        // Commit atual: a árvore dele é a base do diff e ele se torna o pai do novo commit.
        var currentCommit = await Client.Git.Commit
            .Get(owner, repository, headReference)
            .ConfigureAwait(false);

        var treeItems = new List<NewTreeItem>(fileContents.Count);

        foreach (var file in fileContents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var blob = await Client.Git.Blob
                .Create(owner, repository, new NewBlob
                {
                    Content = file.Value,
                    Encoding = EncodingType.Utf8
                })
                .ConfigureAwait(false);

            treeItems.Add(new NewTreeItem
            {
                Path = file.Key,
                Mode = FileMode,
                Type = TreeType.Blob,
                Sha = blob.Sha
            });
        }

        // A BaseTree preserva todo o conteúdo já versionado e aplica somente as alterações acima.
        // No Octokit 14 a coleção NewTree.Tree é somente leitura e já vem inicializada.
        var newTree = new NewTree { BaseTree = currentCommit.Tree.Sha };

        foreach (var treeItem in treeItems)
        {
            newTree.Tree.Add(treeItem);
        }

        var createdTree = await Client.Git.Tree
            .Create(owner, repository, newTree)
            .ConfigureAwait(false);

        var newCommit = await Client.Git.Commit
            .Create(owner, repository, new NewCommit(
                commitMessage,
                createdTree.Sha,
                currentCommit.Parents.Select(parent => parent.Sha)))
            .ConfigureAwait(false);

        await Client.Git.Reference
            .Update(owner, repository, headReference, new ReferenceUpdate(newCommit.Sha))
            .ConfigureAwait(false);

        return newCommit.Sha;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A versão 14 do Octokit não expõe overloads com <see cref="CancellationToken"/> nas rotas de
    /// Git/pull request; por isso o token é validado no início da operação.
    /// </remarks>
    public async Task<string> OpenPullRequestAsync(string branchName, string title, string description, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        EnsureRepositoryConfiguration();
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var newPullRequest = new NewPullRequest(title, branchName, _options.BaseBranch)
        {
            Body = description
        };

        var pullRequest = await Client.PullRequest
            .Create(_options.Owner, _options.Repository, newPullRequest)
            .ConfigureAwait(false);

        return pullRequest.HtmlUrl;
    }

    /// <summary>Valida os valores vindos de configuração (IOptions) antes de sair para a rede.</summary>
    private void EnsureRepositoryConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_options.Owner))
        {
            throw new InvalidOperationException($"{nameof(GitHubOptions)}.{nameof(GitHubOptions.Owner)} não configurado.");
        }

        if (string.IsNullOrWhiteSpace(_options.Repository))
        {
            throw new InvalidOperationException($"{nameof(GitHubOptions)}.{nameof(GitHubOptions.Repository)} não configurado.");
        }

        if (string.IsNullOrWhiteSpace(_options.BaseBranch))
        {
            throw new InvalidOperationException($"{nameof(GitHubOptions)}.{nameof(GitHubOptions.BaseBranch)} não configurado.");
        }
    }
}
