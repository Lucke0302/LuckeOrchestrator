using Microsoft.EntityFrameworkCore;
using OrquestradorLucke.Application.Interfaces;
using OrquestradorLucke.Domain;

namespace OrquestradorLucke.Infrastructure.Data.Repositories;

/// <summary>
/// Contas autenticáveis da plataforma em PostgreSQL. As duas buscas usam as chaves naturais da tabela —
/// o índice único de <c>username</c> (nome de login já normalizado) e o de <c>refresh_token</c> — e
/// leem sem rastreamento, porque quem grava na sequência é o <c>Update</c>: o change tracker não
/// disputa a instância devolvida ao caso de uso.
/// </summary>
public sealed class UserRepository(AppDbContext context) : IUserRepository
{
    /// <inheritdoc />
    public async Task<User?> GetByUsernameAsync(string username, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        return await context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(user => user.Username == username, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<User?> GetByRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        return await context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(user => user.RefreshToken == refreshToken, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateAsync(User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        // A conta chega destacada do contexto (veio de uma leitura sem rastreamento), por isso Update
        // anexa e grava todas as colunas do record — as refresh token/propriedades init-only são
        // escritas pelos accessors do EF.
        context.Users.Update(user);

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AddAsync(User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        // O Id vem do domínio (Guid.NewGuid()) e o hash já veio calculado do caso de uso.
        context.Users.Add(user);

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> AnyAsync(CancellationToken cancellationToken)
        => await context.Users
            .AsNoTracking()
            .AnyAsync(cancellationToken)
            .ConfigureAwait(false);
}
