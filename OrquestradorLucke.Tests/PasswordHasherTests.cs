using FluentAssertions;
using OrquestradorLucke.Application.Services;

namespace OrquestradorLucke.Tests;

/// <summary>
/// Hash de senha do login: SHA256 em hexadecimal, determinístico, e a verificação que nunca compara a
/// senha em claro — só o hash (em tempo constante).
/// </summary>
public sealed class PasswordHasherTests
{
    [Fact]
    public void ComputeHash_DeveSerDeterministicoEHexagonalDeSha256()
    {
        var first = PasswordHasher.ComputeHash("senha-do-painel");
        var second = PasswordHasher.ComputeHash("senha-do-painel");

        first.Should().Be(second);
        first.Should().HaveLength(PasswordHasher.HashLength);

        // Somente caracteres hexadecimais minúsculos: é o formato aceito de volta em FromHexString.
        first.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void ComputeHash_ComSenhasDiferentes_DeveProduzirHashesDiferentes()
    {
        PasswordHasher.ComputeHash("senha-do-painel")
            .Should()
            .NotBe(PasswordHasher.ComputeHash("senha-do-paine1"));
    }

    [Fact]
    public void Verify_ComASenhaCorreta_DeveAprovar()
    {
        var hash = PasswordHasher.ComputeHash("senha-do-painel");

        PasswordHasher.Verify("senha-do-painel", hash).Should().BeTrue();
    }

    [Fact]
    public void Verify_ComSenhaErrada_DeveRecusar()
    {
        var hash = PasswordHasher.ComputeHash("senha-do-painel");

        PasswordHasher.Verify("outra-senha", hash).Should().BeFalse();
    }

    [Fact]
    public void Verify_ComHashCorrompido_DeveRecusarSemExcecao()
    {
        // Hash gravado fora do padrão hex (linha editada à mão no banco): recusa, não estoura 500.
        PasswordHasher.Verify("senha-do-painel", "nao-e-hexadecimal").Should().BeFalse();
    }

    [Fact]
    public void Verify_SemSenhaOuSemHash_DeveRecusar()
    {
        var hash = PasswordHasher.ComputeHash("senha-do-painel");

        PasswordHasher.Verify(string.Empty, hash).Should().BeFalse();
        PasswordHasher.Verify("senha-do-painel", string.Empty).Should().BeFalse();
    }
}
