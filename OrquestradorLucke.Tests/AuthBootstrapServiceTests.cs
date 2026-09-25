using FluentAssertions;
using Moq;
using OrquestradorLucke.Application.Configuration;
using OrquestradorLucke.Application.Interfaces;
using OrquestradorLucke.Application.Services;
using OrquestradorLucke.Domain;

namespace OrquestradorLucke.Tests;

/// <summary>
/// Seed do usuário inicial: sem conta o login (e a API protegida) seria inalcançável, mas o seed só
/// escreve com as credenciais configuradas e a tabela vazia — e grava hash SHA256, nunca a senha.
/// </summary>
public sealed class AuthBootstrapServiceTests
{
    [Fact]
    public async Task SemCredenciaisConfiguradas_NaoDeveCriarConta()
    {
        var repository = new Mock<IUserRepository>();
        var service = new AuthBootstrapService(repository.Object, new AuthBootstrapSettings());

        var created = await service.EnsureBootstrapUserAsync(CancellationToken.None);

        created.Should().BeFalse();

        // Nem chega a consultar: sem usuário/senha na configuração não há o que semear.
        repository.Verify(
            candidate => candidate.AnyAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ComTabelaVazia_DeveCriarAContaComONomeNormalizadoEHashSha256()
    {
        var repository = new Mock<IUserRepository>();
        var added = new List<User>();

        repository
            .Setup(candidate => candidate.AnyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        repository
            .Setup(candidate => candidate.AddAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()))
            .Callback<User, CancellationToken>((user, _) => added.Add(user))
            .Returns(Task.CompletedTask);

        var service = new AuthBootstrapService(
            repository.Object,
            new AuthBootstrapSettings { Username = " ADMIN ", Password = "senha-do-painel" });

        var created = await service.EnsureBootstrapUserAsync(CancellationToken.None);

        created.Should().BeTrue();
        added.Should().ContainSingle();
        added[0].Username.Should().Be("admin");
        added[0].PasswordHash.Should().Be(PasswordHasher.ComputeHash("senha-do-painel"));

        // A chave é gerada explicitamente: a coluna 'users.id' não tem DEFAULT no PostgreSQL (a
        // migration não cria gerador), então um Guid vazio seria recusado pelo banco.
        added[0].Id.Should().NotBe(Guid.Empty);

        // A senha em claro não existe na entidade que vai para o banco.
        added[0].PasswordHash.Should().NotBe("senha-do-painel");
        added[0].RefreshToken.Should().BeNull();
    }

    [Fact]
    public async Task ComContasExistentes_NaoDeveSobrescreverASenha()
    {
        var repository = new Mock<IUserRepository>();

        repository
            .Setup(candidate => candidate.AnyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var service = new AuthBootstrapService(
            repository.Object,
            new AuthBootstrapSettings { Username = "admin", Password = "senha-do-painel" });

        var created = await service.EnsureBootstrapUserAsync(CancellationToken.None);

        // A senha em vigor é a do banco: um restart não pode reaplicar a senha da configuração.
        created.Should().BeFalse();
        repository.Verify(
            candidate => candidate.AddAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
