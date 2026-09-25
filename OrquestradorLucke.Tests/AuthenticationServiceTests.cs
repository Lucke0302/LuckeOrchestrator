using FluentAssertions;
using Moq;
using OrquestradorLucke.Application.Configuration;
using OrquestradorLucke.Application.Interfaces;
using OrquestradorLucke.Application.Models;
using OrquestradorLucke.Application.Services;
using OrquestradorLucke.Domain;

namespace OrquestradorLucke.Tests;

/// <summary>
/// Casos de uso de autenticação: login (hash SHA256, sem revelar ao cliente se a conta existe) e
/// refresh rotativo (cada uso grava um valor novo; token desconhecido ou vencido exige login de novo).
/// </summary>
public sealed class AuthenticationServiceTests
{
    private const string ValidPassword = "senha-do-painel";

    [Fact]
    public async Task Login_ComCredenciaisValidas_DeveEmitirEPersistirORefreshToken()
    {
        var user = CreateUser(ValidPassword);
        var repository = CreateRepository(userByUsername: user);
        var (service, persisted) = CreateService(repository);

        var result = await service.LoginAsync("admin", ValidPassword, CancellationToken.None);

        result.Outcome.Should().Be(AuthenticationOutcome.Sucesso);
        result.Tokens.Should().NotBeNull();

        // O refresh token devolvido ao cliente é o MESMO gravado no banco, com a janela de 7 dias.
        persisted.Should().ContainSingle();
        persisted[0].Id.Should().Be(user.Id);
        persisted[0].RefreshToken.Should().Be(result.Tokens!.RefreshToken);
        persisted[0].RefreshTokenExpiry.Should().BeCloseTo(
            DateTimeOffset.UtcNow.AddDays(7),
            TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Login_DeveGravarUmRefreshTokenDiferenteDoAnterior()
    {
        var user = CreateUser(ValidPassword, refreshToken: "refresh-vigente");
        var repository = CreateRepository(userByUsername: user);
        var (service, persisted) = CreateService(repository);

        var result = await service.LoginAsync("admin", ValidPassword, CancellationToken.None);

        // A rotação substitui o valor: o token antigo deixa de valer no banco.
        persisted[0].RefreshToken.Should().NotBe("refresh-vigente");
        persisted[0].RefreshToken.Should().Be(result.Tokens!.RefreshToken);
    }

    [Fact]
    public async Task Login_DeveNormalizarONomeDeLoginAntesDeConsultar()
    {
        var user = CreateUser(ValidPassword);
        var repository = CreateRepository(userByUsername: user);
        var (service, _) = CreateService(repository);

        await service.LoginAsync("  ADMIN  ", ValidPassword, CancellationToken.None);

        // O domínio grava/compara o login na forma canônica (minúsculo, sem espaços nas pontas).
        repository.Verify(
            candidate => candidate.GetByUsernameAsync("admin", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Login_ComSenhaIncorreta_DeveRecusarSemPersistir()
    {
        var user = CreateUser(ValidPassword);
        var repository = CreateRepository(userByUsername: user);
        var (service, persisted) = CreateService(repository);

        var result = await service.LoginAsync("admin", "senha-errada", CancellationToken.None);

        result.Outcome.Should().Be(AuthenticationOutcome.CredenciaisInvalidas);
        result.Tokens.Should().BeNull();
        persisted.Should().BeEmpty();
    }

    [Fact]
    public async Task Login_ComContaInexistente_DeveRecusarComOMesmoDesfechoDaSenhaErrada()
    {
        var repository = CreateRepository(userByUsername: null);
        var (service, persisted) = CreateService(repository);

        var result = await service.LoginAsync("fantasma", ValidPassword, CancellationToken.None);

        // Nada distingue "não existe" de "senha errada" na resposta ao cliente.
        result.Outcome.Should().Be(AuthenticationOutcome.CredenciaisInvalidas);
        persisted.Should().BeEmpty();
    }

    [Fact]
    public async Task Login_SemUsuarioOuSenha_NaoDeveConsultarORepositorio()
    {
        var repository = CreateRepository();
        var (service, _) = CreateService(repository);

        var result = await service.LoginAsync("   ", "   ", CancellationToken.None);

        result.Outcome.Should().Be(AuthenticationOutcome.CredenciaisInvalidas);
        repository.Verify(
            candidate => candidate.GetByUsernameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Refresh_ComTokenVigente_DeveRotacionarOParENovoRefresh()
    {
        var user = CreateUser(ValidPassword, refreshToken: "refresh-vigente", refreshTokenDays: 7);
        var repository = CreateRepository(userByRefreshToken: user);
        var (service, persisted) = CreateService(repository);

        var result = await service.RefreshAsync("refresh-vigente", CancellationToken.None);

        result.Outcome.Should().Be(AuthenticationOutcome.Sucesso);
        result.Tokens!.RefreshToken.Should().NotBe("refresh-vigente");

        persisted.Should().ContainSingle();
        persisted[0].RefreshToken.Should().Be(result.Tokens.RefreshToken);
        persisted[0].RefreshTokenExpiry.Should().BeCloseTo(
            DateTimeOffset.UtcNow.AddDays(7),
            TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Refresh_ComTokenExpirado_DeveExigirLoginNovo()
    {
        var user = CreateUser(ValidPassword, refreshToken: "refresh-vencido", refreshTokenDays: -1);
        var repository = CreateRepository(userByRefreshToken: user);
        var (service, persisted) = CreateService(repository);

        var result = await service.RefreshAsync("refresh-vencido", CancellationToken.None);

        result.Outcome.Should().Be(AuthenticationOutcome.RefreshTokenInvalido);
        persisted.Should().BeEmpty();
    }

    [Fact]
    public async Task Refresh_ComTokenDesconhecido_DeveRecusar()
    {
        var repository = CreateRepository(userByRefreshToken: null);
        var (service, _) = CreateService(repository);

        var result = await service.RefreshAsync("token-que-ninguem-emitiu", CancellationToken.None);

        result.Outcome.Should().Be(AuthenticationOutcome.RefreshTokenInvalido);
        result.Tokens.Should().BeNull();
    }

    [Fact]
    public async Task Refresh_SemToken_NaoDeveConsultarORepositorio()
    {
        var repository = CreateRepository();
        var (service, _) = CreateService(repository);

        var result = await service.RefreshAsync("  ", CancellationToken.None);

        result.Outcome.Should().Be(AuthenticationOutcome.RefreshTokenInvalido);
        repository.Verify(
            candidate => candidate.GetByRefreshTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>Monta o caso de uso com o repositório dublado e captura o que foi persistido.</summary>
    private static (AuthenticationService Service, List<User> Persisted) CreateService(Mock<IUserRepository> repository)
    {
        var persisted = new List<User>();

        repository
            .Setup(candidate => candidate.UpdateAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()))
            .Callback<User, CancellationToken>((user, _) => persisted.Add(user))
            .Returns(Task.CompletedTask);

        var service = new AuthenticationService(
            repository.Object,
            new StubAccessTokenProvider(),
            new JwtOptions { RefreshTokenDays = 7 });

        return (service, persisted);
    }

    /// <summary>Repositório dublado: responde as duas buscas do caso de uso com o que o teste definir.</summary>
    private static Mock<IUserRepository> CreateRepository(User? userByUsername = null, User? userByRefreshToken = null)
    {
        var repository = new Mock<IUserRepository>();

        repository
            .Setup(candidate => candidate.GetByUsernameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(userByUsername);

        repository
            .Setup(candidate => candidate.GetByRefreshTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(userByRefreshToken);

        return repository;
    }

    private static User CreateUser(string password, string? refreshToken = null, int? refreshTokenDays = null)
        => new()
        {
            Username = "admin",
            PasswordHash = PasswordHasher.ComputeHash(password),
            RefreshToken = refreshToken,
            RefreshTokenExpiry = refreshTokenDays is null
                ? null
                : DateTimeOffset.UtcNow.AddDays(refreshTokenDays.Value)
        };

    /// <summary>
    /// Provedor de token dublado: o valor exato do JWT é responsabilidade do adapter, não do caso de uso.
    /// </summary>
    private sealed class StubAccessTokenProvider : IAccessTokenProvider
    {
        public AccessToken CreateAccessToken(User user)
            => new("access-token-de-teste", DateTimeOffset.UtcNow.AddMinutes(15));
    }
}
