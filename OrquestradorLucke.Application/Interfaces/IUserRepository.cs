using OrquestradorLucke.Domain;

namespace OrquestradorLucke.Application.Interfaces;

/// <summary>
/// Persistência das contas autenticáveis da plataforma. A implementação real (PostgreSQL/EF Core) vive
/// na Infrastructure; a Application conhece apenas o contrato.
/// </summary>
public interface IUserRepository
{
    /// <summary>Busca a conta pelo nome de login já normalizado (<c>User.NormalizeUsername</c>).</summary>
    /// <param name="username">Nome de login na forma canônica gravada na coluna <c>username</c>.</param>
    /// <param name="cancellationToken">Token de cancelamento da requisição HTTP.</param>
    /// <returns>A conta encontrada ou <c>null</c> quando o login não existe.</returns>
    Task<User?> GetByUsernameAsync(string username, CancellationToken cancellationToken);

    /// <summary>Busca a conta dona do refresh token informado (índice único da coluna).</summary>
    /// <param name="refreshToken">Refresh token apresentado no <c>POST /api/auth/refresh</c>.</param>
    /// <param name="cancellationToken">Token de cancelamento da requisição HTTP.</param>
    /// <returns>A conta encontrada ou <c>null</c> quando o token não corresponde a ninguém.</returns>
    Task<User?> GetByRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken);

    /// <summary>Persiste o estado atual da conta (refresh token rotacionado e sua validade).</summary>
    /// <param name="user">Conta com os valores finais a gravar.</param>
    /// <param name="cancellationToken">Token de cancelamento da requisição HTTP.</param>
    Task UpdateAsync(User user, CancellationToken cancellationToken);

    /// <summary>Insere uma conta nova (seed do usuário inicial; o <c>Id</c> vem do domínio).</summary>
    /// <param name="user">Conta já com <c>Username</c>/<c>PasswordHash</c> definidos.</param>
    /// <param name="cancellationToken">Token de cancelamento da operação.</param>
    Task AddAsync(User user, CancellationToken cancellationToken);

    /// <summary>Informa se já existe alguma conta cadastrada (o seed só roda com a tabela vazia).</summary>
    /// <param name="cancellationToken">Token de cancelamento da operação.</param>
    Task<bool> AnyAsync(CancellationToken cancellationToken);
}
