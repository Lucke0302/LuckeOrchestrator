using FluentAssertions;
using Moq;
using OrquestradorLucke.Application.Interfaces;
using OrquestradorLucke.Application.Models;
using OrquestradorLucke.Application.Services;
using OrquestradorLucke.Domain;

namespace OrquestradorLucke.Tests;

/// <summary>
/// Casos de uso da API de gerenciamento/revisão: enfileiramento (UUID + <c>Pendente</c> gerados no
/// domínio), aprovação (merge pelo token administrativo + status <c>Aprovada</c>) e rejeição (PR
/// fechado, falha registrada no medidor de frustração compartilhado com o laço e tarefa devolvida para
/// <c>Pendente</c>).
/// </summary>
public sealed class TaskReviewServiceTests
{
    private const string Branch = "feat/task-abc";
    private const string PullRequestUrl = "https://github.com/acme/repo/pull/7";

    [Fact]
    public async Task CreateTaskAsync_DeveEnfileirarComUuidStatusPendenteEComplexidade()
    {
        var repository = new Mock<IAgentTaskRepository>();
        AgentTask? enqueued = null;

        repository
            .Setup(candidate => candidate.AddTaskAsync(It.IsAny<AgentTask>(), It.IsAny<CancellationToken>()))
            .Callback<AgentTask, CancellationToken>((task, _) => enqueued = task)
            .Returns(Task.CompletedTask);

        var service = CreateService(repository);

        var task = await service.CreateTaskAsync(
            "Criar endpoint de cálculo de frete",
            TaskComplexity.Medio,
            CancellationToken.None);

        task.Id.Should().NotBeEmpty();
        task.Status.Should().Be(AgentTaskStatus.Pendente);
        task.Complexidade.Should().Be(TaskComplexity.Medio);
        task.Payload.Should().Be("Criar endpoint de cálculo de frete");
        task.CriadoEm.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));

        enqueued.Should().BeSameAs(task);
    }

    [Fact]
    public async Task GetTasksAsync_DeveRespeitarOTetoEOPadraoDoCasoDeUso()
    {
        var repository = new Mock<IAgentTaskRepository>();
        int? captured = null;

        repository
            .Setup(candidate => candidate.GetTasksAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<int, CancellationToken>((limit, _) => captured = limit)
            .ReturnsAsync(Array.Empty<AgentTask>());

        var service = CreateService(repository);

        await service.GetTasksAsync(limit: null, cancellationToken: CancellationToken.None);
        captured.Should().Be(TaskReviewService.DefaultTaskListLimit);

        // Um painel que pede a tabela inteira recebe o teto: a resposta HTTP não cresce sem limite.
        await service.GetTasksAsync(limit: 5000, cancellationToken: CancellationToken.None);
        captured.Should().Be(TaskReviewService.MaxTaskListLimit);

        await service.GetTasksAsync(limit: 0, cancellationToken: CancellationToken.None);
        captured.Should().Be(1);
    }

    [Fact]
    public async Task AcceptAsync_DeveMesclarOPullRequestEMarcarAprovada()
    {
        var task = CreateDeliveredTask();
        var repository = CreateRepositoryWith(task);
        var gitHub = new Mock<IGitHubService>();
        string? mergedBranch = null;

        gitHub
            .Setup(service => service.MergePullRequestAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((branch, _, _) => mergedBranch = branch)
            .ReturnsAsync("sha-do-merge");

        var tracker = new FrustrationTracker(limiteMaximo: 3);
        var service = CreateService(repository, tracker, gitHub);

        var result = await service.AcceptAsync(task.Id, CancellationToken.None);

        result.Outcome.Should().Be(TaskReviewOutcome.Aprovada);
        result.Succeeded.Should().BeTrue();
        result.Task!.Status.Should().Be(AgentTaskStatus.Aprovada);
        result.Task.AtualizadoEm.Should().NotBeNull();
        result.Task.PullRequestUrl.Should().Be(PullRequestUrl);

        // O merge é pedido pela branch da tarefa (o adapter resolve o token administrativo).
        mergedBranch.Should().Be(Branch);

        repository.Verify(
            candidate => candidate.UpdateTaskAsync(
                It.Is<AgentTask>(updated =>
                    updated.Id == task.Id && updated.Status == AgentTaskStatus.Aprovada),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Aprovar não é falha: o medidor que dispara o overdrive fica intacto.
        tracker.ContadorAtual.Should().Be(0);
    }

    [Fact]
    public async Task AcceptAsync_TarefaSemPullRequest_NaoDeveChamarOGitHubNemGravarStatus()
    {
        var task = new AgentTask
        {
            Payload = "Criar endpoint de frete",
            Status = AgentTaskStatus.Falhou
        };

        var repository = CreateRepositoryWith(task);
        var gitHub = new Mock<IGitHubService>();
        var service = CreateService(repository, gitHubService: gitHub);

        var result = await service.AcceptAsync(task.Id, CancellationToken.None);

        result.Outcome.Should().Be(TaskReviewOutcome.SemPullRequest);
        result.Succeeded.Should().BeFalse();
        result.Detail.Should().Contain("pull request");

        gitHub.Verify(
            candidate => candidate.MergePullRequestAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        repository.Verify(
            candidate => candidate.UpdateTaskAsync(It.IsAny<AgentTask>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AcceptAsync_TarefaInexistente_DeveDevolverNaoEncontrada()
    {
        var taskId = Guid.NewGuid();
        var repository = CreateRepositoryWith(null);
        var service = CreateService(repository);

        var result = await service.AcceptAsync(taskId, CancellationToken.None);

        result.Outcome.Should().Be(TaskReviewOutcome.NaoEncontrada);
        result.Task.Should().BeNull();
        result.Detail.Should().Contain(taskId.ToString());
    }

    [Fact]
    public async Task AcceptAsync_FalhaNoMerge_NaoDeveMarcarAprovada()
    {
        var task = CreateDeliveredTask();
        var repository = CreateRepositoryWith(task);
        var gitHub = new Mock<IGitHubService>();

        gitHub
            .Setup(service => service.MergePullRequestAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("O GitHub recusou o merge do pull request #7"));

        var service = CreateService(repository, gitHubService: gitHub);

        var act = async () => await service.AcceptAsync(task.Id, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*recusou o merge*");

        // Nenhuma exceção do Octokit é engolida: a tarefa permanece no status anterior para uma nova
        // tentativa do revisor, em vez de virar Aprovada sem merge.
        repository.Verify(
            candidate => candidate.UpdateTaskAsync(It.IsAny<AgentTask>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RejectAsync_DeveFecharOPullRequestAlimentarAFrustracaoEDevolverParaAFila()
    {
        var task = CreateDeliveredTask();
        var repository = CreateRepositoryWith(task);
        var gitHub = new Mock<IGitHubService>();
        string? closedBranch = null;
        string? informedReason = null;

        gitHub
            .Setup(service => service.ClosePullRequestAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((branch, reason, _) =>
            {
                closedBranch = branch;
                informedReason = reason;
            })
            .Returns(Task.CompletedTask);

        var tracker = new FrustrationTracker(limiteMaximo: 3);
        var service = CreateService(repository, tracker, gitHub);

        var result = await service.RejectAsync(task.Id, "Faltou validação de entrada", CancellationToken.None);

        result.Outcome.Should().Be(TaskReviewOutcome.Rejeitada);
        result.Succeeded.Should().BeTrue();
        result.Task!.Status.Should().Be(AgentTaskStatus.Pendente);
        result.Task.AtualizadoEm.Should().NotBeNull();

        closedBranch.Should().Be(Branch);
        informedReason.Should().Be("Faltou validação de entrada");

        // O motivo vira memória do daemon: é o texto que o overdrive recebe no próximo ciclo.
        tracker.ContadorAtual.Should().Be(1);
        tracker.HistoricoFalhas.Should().ContainSingle()
            .Which.Should().Contain("revisão humana").And.Contain("Faltou validação de entrada");
        tracker.OverdriveDisparado.Should().BeFalse();

        repository.Verify(
            candidate => candidate.UpdateTaskAsync(
                It.Is<AgentTask>(updated =>
                    updated.Id == task.Id && updated.Status == AgentTaskStatus.Pendente),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RejectAsync_TarefaSemBranch_DeveAlimentarAFrustracaoSemChamarOGitHub()
    {
        var task = new AgentTask
        {
            Payload = "Criar endpoint de frete",
            Status = AgentTaskStatus.Falhou
        };

        var repository = CreateRepositoryWith(task);
        var gitHub = new Mock<IGitHubService>();
        var tracker = new FrustrationTracker(limiteMaximo: 3);
        var service = CreateService(repository, tracker, gitHub);

        var result = await service.RejectAsync(task.Id, "Implementação incompleta", CancellationToken.None);

        // Sem PR aberto não há o que fechar, mas a rejeição do revisor continua valendo.
        result.Outcome.Should().Be(TaskReviewOutcome.Rejeitada);
        result.Task!.Status.Should().Be(AgentTaskStatus.Pendente);
        tracker.ContadorAtual.Should().Be(1);

        gitHub.Verify(
            candidate => candidate.ClosePullRequestAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RejectAsync_MotivoEmBranco_NaoDeveSerAceito()
    {
        var task = CreateDeliveredTask();
        var repository = CreateRepositoryWith(task);
        var service = CreateService(repository);

        var act = async () => await service.RejectAsync(task.Id, "   ", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RejectAsync_TarefaInexistente_DeveDevolverNaoEncontradaSemAlimentarAFrustracao()
    {
        var repository = CreateRepositoryWith(null);
        var tracker = new FrustrationTracker(limiteMaximo: 3);
        var service = CreateService(repository, tracker);

        var result = await service.RejectAsync(Guid.NewGuid(), "Não encontrada", CancellationToken.None);

        result.Outcome.Should().Be(TaskReviewOutcome.NaoEncontrada);
        tracker.ContadorAtual.Should().Be(0);
    }

    /// <summary>Serviço sob teste com o medidor real (o contador é justamente o que os testes olham).</summary>
    private static TaskReviewService CreateService(
        Mock<IAgentTaskRepository> repository,
        FrustrationTracker? tracker = null,
        Mock<IGitHubService>? gitHubService = null)
        => new(
            repository.Object,
            (gitHubService ?? new Mock<IGitHubService>()).Object,
            tracker ?? new FrustrationTracker(limiteMaximo: 3));

    /// <summary>Repositório que devolve sempre a tarefa informada (ou nenhuma).</summary>
    private static Mock<IAgentTaskRepository> CreateRepositoryWith(AgentTask? task)
    {
        var repository = new Mock<IAgentTaskRepository>();

        repository
            .Setup(candidate => candidate.GetTaskByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(task);

        return repository;
    }

    /// <summary>
    /// Tarefa já entregue: branch e pull request abertos, aguardando a revisão humana.
    /// </summary>
    private static AgentTask CreateDeliveredTask()
        => new()
        {
            Payload = "Criar endpoint de cálculo de frete",
            Complexidade = TaskComplexity.Medio,
            Status = AgentTaskStatus.Concluida,
            Branch = Branch,
            PullRequestUrl = PullRequestUrl
        };
}
