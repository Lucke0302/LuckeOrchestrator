using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OrquestradorLucke.Application.Configuration;
using OrquestradorLucke.Application.Interfaces;
using OrquestradorLucke.Application.Models;
using OrquestradorLucke.Domain;

namespace OrquestradorLucke.Infrastructure.Security;

/// <summary>
/// Assinatura dos access tokens JWT (HS256, chave simétrica de <c>Jwt:Secret</c>). É a única classe que
/// conhece as bibliotecas de token: a Application enxerga apenas <see cref="IAccessTokenProvider"/>.
/// </summary>
/// <remarks>
/// As claims são as registradas do JWT, gravadas literalmente (sem o mapeamento de tipos de claim do
/// host): <c>sub</c> = Id da conta e <c>unique_name</c> = nome de login. O segredo nunca é hardcoded —
/// vem de <see cref="JwtOptions"/> (user-secrets/variável de ambiente) e a configuração é validada
/// antes de assinar, para que um segredo ausente ou curto demais falhe no start e não no login.
/// </remarks>
public sealed class JwtTokenProvider(IOptions<JwtOptions> options) : IAccessTokenProvider
{
    /// <summary>
    /// Assinador reutilizado entre as requisições: é thread-safe e caro de construir a cada emissão (o
    /// registro é Singleton, então uma instância por processo basta).
    /// </summary>
    private static readonly JsonWebTokenHandler TokenHandler = new();

    /// <inheritdoc />
    public AccessToken CreateAccessToken(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var jwt = options.Value;
        jwt.EnsureUsable();

        var issuedAt = DateTimeOffset.UtcNow;
        var expiresAt = issuedAt.Add(jwt.AccessTokenLifetime);

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.UniqueName, user.Username),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            ]),
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            IssuedAt = issuedAt.UtcDateTime,
            NotBefore = issuedAt.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret)),
                SecurityAlgorithms.HmacSha256)
        };

        return new AccessToken(TokenHandler.CreateToken(descriptor), expiresAt);
    }
}
