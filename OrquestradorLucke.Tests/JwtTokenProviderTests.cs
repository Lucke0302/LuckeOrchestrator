using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OrquestradorLucke.Application.Configuration;
using OrquestradorLucke.Application.Services;
using OrquestradorLucke.Domain;
using OrquestradorLucke.Infrastructure.Security;

namespace OrquestradorLucke.Tests;

/// <summary>
/// Emissão do access token: assinatura HS256 com o segredo configurado, claims registradas da conta
/// (<c>sub</c>/<c>unique_name</c>) e a validade de <c>Jwt:AccessTokenMinutes</c>. O token é conferido com
/// os MESMOS parâmetros que o <c>JwtBearer</c> do host usa em <c>AddJwtAuthentication</c>.
/// </summary>
public sealed class JwtTokenProviderTests
{
    private const string Secret = "segredo-de-teste-com-tamanho-suficiente-para-hs256";
    private const string Issuer = "OrquestradorLucke.Tests";
    private const string Audience = "OrquestradorLucke.Tests.Panel";

    [Fact]
    public async Task CreateAccessToken_DeveAssinarTokenValidoComAsClaimsDaConta()
    {
        var user = new User { Username = "admin", PasswordHash = PasswordHasher.ComputeHash("senha-do-painel") };
        var provider = CreateProvider();

        var issued = provider.CreateAccessToken(user);

        issued.Token.Should().NotBeNullOrWhiteSpace();
        issued.ExpiresAtUtc.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(15), TimeSpan.FromSeconds(5));

        var validation = await ValidateAsync(issued.Token);

        validation.IsValid.Should().BeTrue();
        validation.Claims[JwtRegisteredClaimNames.Sub].Should().Be(user.Id.ToString());
        validation.Claims[JwtRegisteredClaimNames.UniqueName].Should().Be("admin");
    }

    [Fact]
    public async Task CreateAccessToken_ComOutroSegredo_NaoDeveValidar()
    {
        var provider = CreateProvider();
        var issued = provider.CreateAccessToken(new User
        {
            Username = "admin",
            PasswordHash = PasswordHasher.ComputeHash("senha-do-painel")
        });

        // Chave diferente da que assinou: é exatamente o que o host rejeita com 401.
        var validation = await ValidateAsync(issued.Token, "outro-segredo-de-teste-com-tamanho-suficiente");

        validation.IsValid.Should().BeFalse();
    }

    [Fact]
    public void CreateAccessToken_SemSegredo_DeveFalhar()
    {
        var provider = new JwtTokenProvider(Options.Create(new JwtOptions()));

        var act = () => provider.CreateAccessToken(new User
        {
            Username = "admin",
            PasswordHash = PasswordHasher.ComputeHash("senha-do-painel")
        });

        act.Should().Throw<InvalidOperationException>().WithMessage("*Jwt:Secret*");
    }

    private static JwtTokenProvider CreateProvider()
        => new(Options.Create(new JwtOptions
        {
            Secret = Secret,
            Issuer = Issuer,
            Audience = Audience,
            AccessTokenMinutes = 15
        }));

    private static async Task<TokenValidationResult> ValidateAsync(string token, string secret = Secret)
        => await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        });
}
