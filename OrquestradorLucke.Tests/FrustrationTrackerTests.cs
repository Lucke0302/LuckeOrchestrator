using FluentAssertions;
using OrquestradorLucke.Domain;

namespace OrquestradorLucke.Tests;

/// <summary>
/// Comportamento do medidor de frustração (<see cref="FrustrationTracker"/>): cada falha alimenta o
/// contador e o histórico de motivos, o overdrive dispara exatamente no limite e um sucesso zera os
/// dois — a memória que o worker entrega ao modelo mais robusto.
/// </summary>
public sealed class FrustrationTrackerTests
{
    [Fact]
    public void RegistrarFalha_ComMotivo_DeveAcumularNoHistorico()
    {
        var tracker = new FrustrationTracker(limiteMaximo: 3);

        tracker.RegistrarFalha("gemma: retorno sem arquivos");
        tracker.RegistrarFalha("gemma: resposta não parseável");

        tracker.ContadorAtual.Should().Be(2);
        tracker.HistoricoFalhas.Should().Equal(
            "gemma: retorno sem arquivos",
            "gemma: resposta não parseável");
    }

    [Fact]
    public void RegistrarFalha_NoLimite_DeveDispararOverdrive()
    {
        var tracker = new FrustrationTracker(limiteMaximo: 3);

        tracker.RegistrarFalha("falha 1").Should().BeFalse();
        tracker.RegistrarFalha("falha 2").Should().BeFalse();
        tracker.RegistrarFalha("falha 3").Should().BeTrue();

        tracker.OverdriveDisparado.Should().BeTrue();
        tracker.TentativasRestantes.Should().Be(0);
        tracker.HistoricoFalhas.Should().HaveCount(3);
    }

    [Fact]
    public void RegistrarSucesso_DeveLimparContadorEHistorico()
    {
        var tracker = new FrustrationTracker(limiteMaximo: 3);

        tracker.RegistrarFalha("falha 1");
        tracker.RegistrarFalha("falha 2");

        tracker.RegistrarSucesso();

        tracker.ContadorAtual.Should().Be(0);
        tracker.HistoricoFalhas.Should().BeEmpty();
        tracker.OverdriveDisparado.Should().BeFalse();
    }

    [Fact]
    public void RegistrarFalha_AposDispararOverdrive_DeveManterContadorNoLimite()
    {
        var tracker = new FrustrationTracker(limiteMaximo: 2);

        tracker.RegistrarFalha("falha 1");
        tracker.RegistrarFalha("falha 2");

        // A tentativa do overdrive também pode falhar: o contador não passa do limite, mas o motivo
        // continua entrando no histórico (é o que a próxima escalada precisa saber).
        tracker.RegistrarFalha("falha 3");

        tracker.ContadorAtual.Should().Be(2);
        tracker.HistoricoFalhas.Should().HaveCount(3);
    }

    [Fact]
    public void RegistrarFalha_DeveLimitarOTamanhoDoHistorico()
    {
        var tracker = new FrustrationTracker(limiteMaximo: 100);

        for (var index = 1; index <= 12; index++)
        {
            tracker.RegistrarFalha($"falha {index}");
        }

        // Memória limitada: um daemon de longa duração mantém apenas os motivos mais recentes.
        tracker.HistoricoFalhas.Should().HaveCount(10);
        tracker.HistoricoFalhas[0].Should().Be("falha 3");
        tracker.HistoricoFalhas[^1].Should().Be("falha 12");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RegistrarFalha_ComMotivoInvalido_DeveLancarArgumentException(string? motivo)
    {
        var tracker = new FrustrationTracker(limiteMaximo: 3);

        var act = () => tracker.RegistrarFalha(motivo!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Construtor_ComLimiteInvalido_DeveLancarArgumentOutOfRangeException()
    {
        var act = () => new FrustrationTracker(limiteMaximo: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
