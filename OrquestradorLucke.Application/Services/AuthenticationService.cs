using System.Buffers.Text;
using System.Security.Cryptography;
using OrquestradorLucke.Application.Configuration;
using OrquestradorLucke.Application.Interfaces;
using OrquestradorLucke.Application.Models;
using OrquestradorLucke.Domain;

namespace OrquestradorLucke.Application.Services;

/// <summary>
/// Casos de uso de autenticação da plataforma web: valida as credenciais (<c>login</c>) e renova o par
/// de tokens sem novo login (<c>refresh</c>).
/// </summary>
/// <remarks>
/// <para>
/// Serviço Scoped: consome o repositório de contas (que carrega o <c>DbContext</c> do escopo da
/// requisição HTTP) e o <see cref="IAccessTokenProvider"/> — nunca injetado em um Singleton.
/// </para>
/// <para>
/// O refresh token é <b>rotativo</b>: cada refresh grava um valor novo, então o token anterior deixa
/// de existir no banco (uma cópia vazada vale, no máximo, até o próximo uso legítimo). As validades
/// (15 minutos para o access token, 7 dias para o refresh) vêm de <see cref="JwtOptions"/>.
/// </para>
/// </remarks>
public sealed class AuthenticationService(
    IUserRepository userRepository,
    IAccessTokenProvider accessTokenProvider,
    JwtOptions jwtOptions)
{
    /// <summary>Tamanho, em bytes, da entropia do refresh token (256 bits).</summary>
    private const int RefreshTokenByteLength = 64;

    /// <summary>
    /// Valida as credenciais e, quando conferem, emite e persiste um par de tokens novo.
    /// </summary>
    /// <param name="username">Nome de login informado no painel (normalizado antes da consulta).</param>
    /// <param name="password">Senha em claro; o hash é recalculado e comparado em tempo constante.</param>
    /// <param name="cancellationToken">Token de cancelamento da requisição HTTP.</param>
    /// <returns>
    /// <see cref="AuthenticationOutcome.Sucesso"/> com o par de tokens, ou
    /// <see cref="AuthenticationOutcome.CredenciaisInvalidas"/> — a mesma resposta para conta
    /// inexistente e senha errada, para não confirmar ao cliente se o login existe.
    /// </returns>
    public async Task<AuthenticationResult> LoginAsync(string username, string password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return AuthenticationResult.CredenciaisInvalidas();
        }

        var user = await userRepository
            .GetByUsernameAsync(User.NormalizeUsername(username), cancellationToken)
            .ConfigureAwait(false);

        if (user is null || !PasswordHasher.Verify(password, user.PasswordHash))
        {
            return AuthenticationResult.CredenciaisInvalidas();
        }

        return await RotateTokensAsync(user, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Troca um refresh token válido por um par novo (access + refresh) e grava o valor rotacionado.
    /// </summary>
    /// <param name="refreshToken">Refresh token vigente, como veio do login anterior.</param>
    /// <param name="cancellationToken">Token de cancelamento da requisição HTTP.</param>
    /// <returns>
    /// <see cref="AuthenticationOutcome.Sucesso"/> com o par novo, ou
    /// <see cref="AuthenticationOutcome.RefreshTokenInvalido"/> quando o token é desconhecido, já foi
    /// rotacionado ou expirou.
    /// </returns>
    public async Task<AuthenticationResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return AuthenticationResult.RefreshTokenInvalido();
        }

        var user = await userRepository
            .GetByRefreshTokenAsync(refreshToken.Trim(), cancellationToken)
            .ConfigureAwait(false);

        // Conta sem refresh token (nunca autenticou) ou com a janela vencida: exige login novo.
        if (user?.RefreshTokenExpiry is null || user.RefreshTokenExpiry <= DateTimeOffset.UtcNow)
        {
            return AuthenticationResult.RefreshTokenInvalido();
        }

        return await RotateTokensAsync(user, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Emite o par novo e persiste o refresh rotacionado: o access token é assinado pelo provedor e o
    /// refresh é aleatório, nunca derivado do anterior (não há como prever o próximo valor a partir do
    /// atual).
    /// </summary>
    private async Task<AuthenticationResult> RotateTokensAsync(User user, CancellationToken cancellationToken)
    {
        var accessToken = accessTokenProvider.CreateAccessToken(user);
        var refreshToken = CreateRefreshToken();
        var refreshTokenExpiry = DateTimeOffset.UtcNow.Add(jwtOptions.RefreshTokenLifetime);

        var rotated = user with
        {
            RefreshToken = refreshToken,
            RefreshTokenExpiry = refreshTokenExpiry
        };

        await userRepository.UpdateAsync(rotated, cancellationToken).ConfigureAwait(false);

        return AuthenticationResult.Sucesso(new AuthTokenResponse(
            accessToken.Token,
            refreshToken,
            accessToken.ExpiresAtUtc,
            refreshTokenExpiry));
    }

    /// <summary>
    /// Gera o refresh token: 64 bytes de aleatoriedade criptográfica em Base64Url — sem
    /// <c>+</c>/<c>/</c>/<c>=</c>, portanto seguro em JSON, cabeçalho e query string (é assim que o
    /// hub do SignalR recebe o access token). Não é um GUID nem um valor derivado da senha: previsível
    /// seria o mesmo que não ter token.
    /// </summary>
    private static string CreateRefreshToken()
        => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(RefreshTokenByteLength));
}
