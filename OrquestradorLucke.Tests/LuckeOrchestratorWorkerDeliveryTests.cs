using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using OrquestradorLucke.Application.Configuration;
using OrquestradorLucke.Application.Interfaces;
using OrquestradorLucke.Application.Services;
using OrquestradorLucke.Domain;
using OrquestradorLucke.Worker;
using OrquestradorLucke.Worker.Configuration;

namespace OrquestradorLucke.Tests;

/// <summary>
/// Auditoria da entrega no <see cref="LuckeOrchestratorWorker"/>: falha do GitHub (Octokit) não pode
/// ser engolida — a tarefa termina como <c>Falhou</c>, o erro é logado como <c>Error</c> com o passo
/// que falhou e o histórico de frustração é alimentado (a memória que o overdrive consome). O caminho
/// feliz permanece: arquivos com conteúdo são commitados e a tarefa vira <c>Concluida</c>.
/// </summary>
public sealed class LuckeOrchestratorWorkerDeliveryTests
{
    private const string PullRequestUrl = "https://github.com/acme/repo/pull/7";

    /// <summary>
    /// Resumo do PR devolvido pelo expert de sumarização: o JSON do contrato cercado por crases de
    /// markdown, como os modelos de fato devolvem — é exatamente o caso que exige a sanitização antes
    /// do parse no worker.
    /// </summary>
    private const string PullRequestSummaryJson = """
        ```json
        {"titulo": "feat(alvo): implementa a classe Alvo", "descricao": "## Resumo\n\nCria a classe `Alvo`.\n\n- src/Alvo.cs"}
        ```
        """;

    [Fact]
    public async Task ExecuteAsync_FalhaNoCommitDoGitHub_DeveMarcarFalhouLogarErroEAlimentarAFrustracao()
    {
        var task = new AgentTask
        {
            Payload = "Crie a classe Alvo.",
            Complexidade = TaskComplexity.Baixo
        };

        var failed = new TaskCompletionSource<AgentTask>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = CreateProvider(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["src/Alvo.cs"] = "namespace Alvo; public class Alvo { }"
        });

        using var harness = CreateHarness(
            task,
            provider,
            commitSucceeds: false,
            onStatusUpdate: updated =>
            {
                if (updated.Status == AgentTaskStatus.Falhou)
                {
                    failed.TrySetResult(updated);
                }
            });

        await harness.Worker.StartAsync(CancellationToken.None);

        AgentTask failedTask;

        try
        {
            failedTask = await failed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            await harness.Worker.StopAsync(CancellationToken.None);
        }

        failedTask.Status.Should().Be(AgentTaskStatus.Falhou);
        failedTask.Branch.Should().BeNull();
        failedTask.PullRequestUrl.Should().BeNull();

        // O fluxo parou no commit: nenhum pull request foi aberto e a tarefa não foi concluída.
        harness.GitHubService.Verify(
            service => service.OpenPullRequestAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        harness.Repository.Verify(
            candidate => candidate.UpdateTaskAsync(
                It.Is<AgentTask>(updated => updated.Status == AgentTaskStatus.Concluida),
                It.IsAny<CancellationToken>()),
            Times.Never);

        // Fim do silêncio: UM log de erro, com a exceção do Octokit, o passo que falhou e o contador
        // da frustração já alimentado (1 de 3) — a memória que o overdrive recebe.
        var errors = harness.Logger.Entries.Where(entry => entry.Level == LogLevel.Error).ToList();

        errors.Should().HaveCount(1);
        errors[0].Exception.Should().BeSameAs(harness.CommitFailure);
        errors[0].Message.Should().Contain("Falha de entrega no GitHub").And.Contain("falha 1/3");
    }

    [Fact]
    public async Task ExecuteAsync_EntregaBemSucedida_DeveConcluirTarefaEIgnorarArquivoSemConteudo()
    {
        var task = new AgentTask
        {
            Payload = "Crie as classes.",
            Complexidade = TaskComplexity.Baixo
        };

        var concluded = new TaskCompletionSource<AgentTask>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = CreateProvider(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["src/Alvo.cs"] = "namespace Alvo; public class Alvo { }",
            // Entrada em branco: descartada no parse, não vira arquivo vazio no repositório.
            ["src/Vazio.cs"] = "   "
        });

        using var harness = CreateHarness(
            task,
            provider,
            commitSucceeds: true,
            onStatusUpdate: updated =>
            {
                if (updated.Status == AgentTaskStatus.Concluida)
                {
                    concluded.TrySetResult(updated);
                }
            });

        await harness.Worker.StartAsync(CancellationToken.None);

        AgentTask concludedTask;

        try
        {
            concludedTask = await concluded.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            await harness.Worker.StopAsync(CancellationToken.None);
        }

        // Auditoria do parse: 1 de 2 arquivos é aproveitável.
        harness.Logger.Messages
            .Should()
            .Contain(message => message.Contains("1 de 2 arquivo(s) aproveitável(is)"));

        // Só o arquivo com conteúdo foi para o commit.
        harness.GitHubService.Verify(
            service => service.CommitChangesAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.Is<IReadOnlyDictionary<string, string>>(files =>
                    files.Count == 1 && files.ContainsKey("src/Alvo.cs")),
                It.IsAny<CancellationToken>()),
            Times.Once);

        concludedTask.Status.Should().Be(AgentTaskStatus.Concluida);
        concludedTask.Branch.Should().Be($"feat/task-{task.Id}");
        concludedTask.PullRequestUrl.Should().Be(PullRequestUrl);
    }

    /// <summary>
    /// O resumo do PR vem do contrato JSON do expert: a resposta cercada por crases de markdown passa
    /// pela mesma sanitização dos artefatos antes do parse, então o título e o corpo do pull request são
    /// os do LLM — sem cair no resumo determinístico.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ResumoEmJsonCercadoPorCrases_DevePreencherTituloECorpoDoPullRequest()
    {
        var task = new AgentTask
        {
            Payload = "Crie a classe Alvo.",
            Complexidade = TaskComplexity.Baixo
        };

        var concluded = new TaskCompletionSource<AgentTask>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = CreateProvider(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["src/Alvo.cs"] = "namespace Alvo; public class Alvo { }"
        });

        using var harness = CreateHarness(
            task,
            provider,
            commitSucceeds: true,
            onStatusUpdate: updated =>
            {
                if (updated.Status == AgentTaskStatus.Concluida)
                {
                    concluded.TrySetResult(updated);
                }
            });

        await harness.Worker.StartAsync(CancellationToken.None);

        try
        {
            await concluded.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            await harness.Worker.StopAsync(CancellationToken.None);
        }

        // Título e corpo saem do JSON do contrato (o "```json" foi removido pelo sanitizador).
        harness.GitHubService.Verify(
            service => service.OpenPullRequestAsync(
                $"feat/task-{task.Id}",
                "feat(alvo): implementa a classe Alvo",
                It.Is<string>(body => body.Contains("## Resumo") && body.Contains("src/Alvo.cs")),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Sem fallback acionado: o aviso de resumo padrão não aparece no log.
        harness.Logger.Messages.Should().NotContain(message => message.Contains("usando o resumo padrão"));
    }

    /// <summary>
    /// Resposta fora do contrato do resumo (texto livre): o pull request recebe o título e o corpo
    /// determinísticos e, antes disso, um único warning traz o motivo do parse E a prévia da string
    /// bruta devolvida pelo LLM — a evidência que faltava para diagnosticar o fallback.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ResumoForaDoContrato_DeveLogarPreviaBrutaEUsarResumoPadrao()
    {
        const string RawSummary = "Claro! Segue o resumo do Pull Request: implementa a classe Alvo, sem JSON.";

        var task = new AgentTask
        {
            Payload = "Crie a classe Alvo.",
            Complexidade = TaskComplexity.Baixo
        };

        var concluded = new TaskCompletionSource<AgentTask>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = CreateProvider(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["src/Alvo.cs"] = "namespace Alvo; public class Alvo { }"
            },
            RawSummary);

        using var harness = CreateHarness(
            task,
            provider,
            commitSucceeds: true,
            onStatusUpdate: updated =>
            {
                if (updated.Status == AgentTaskStatus.Concluida)
                {
                    concluded.TrySetResult(updated);
                }
            });

        await harness.Worker.StartAsync(CancellationToken.None);

        try
        {
            await concluded.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            await harness.Worker.StopAsync(CancellationToken.None);
        }

        // Parse quebrado: o PR sai com o título e o corpo padrão da tarefa.
        harness.GitHubService.Verify(
            service => service.OpenPullRequestAsync(
                $"feat/task-{task.Id}",
                $"feat(task-{task.Id})",
                It.Is<string>(body => body.Contains("Entrega automática da tarefa")),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Auditoria do fallback: motivo do parse e prévia da resposta bruta, no mesmo warning.
        var warning = harness.Logger.Entries.Single(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("Falha ao interpretar o resumo do Pull Request"));

        warning.Message.Should().Contain(RawSummary);
        warning.Exception.Should().BeOfType<JsonException>();
    }

    /// <summary>Worker em execução com o grafo mínimo instrumentado (uma tarefa na fila por ciclo).</summary>
    private sealed record Harness(
        LuckeOrchestratorWorker Worker,
        RecordingLogger<LuckeOrchestratorWorker> Logger,
        Mock<IAgentTaskRepository> Repository,
        Mock<IGitHubService> GitHubService,
        Exception CommitFailure,
        ServiceProvider Services) : IDisposable
    {
        public void Dispose() => Services.Dispose();
    }

    /// <summary>
    /// Expert fake: devolve os artefatos informados, o resumo do contrato para o pull request e
    /// identifica-se como um modelo do catálogo.
    /// </summary>
    private static Mock<ILLMProvider> CreateProvider(
        Dictionary<string, string> artifacts,
        string pullRequestSummary = PullRequestSummaryJson)
    {
        var provider = new Mock<ILLMProvider>();

        provider.SetupGet(candidate => candidate.ModelName).Returns("models/gemma-4-26b-a4b-it");
        provider
            .Setup(candidate => candidate.GenerateCodeAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(artifacts);
        provider
            .Setup(candidate => candidate.GeneratePullRequestSummaryAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(pullRequestSummary);

        return provider;
    }

    /// <summary>
    /// Monta o worker com os mocks do ciclo: dequeue de uma tarefa (depois fila vazia), GitHub com
    /// branch criada e commit que pode falhar, RAG sem referências e o logger de captura.
    /// </summary>
    private static Harness CreateHarness(
        AgentTask task,
        Mock<ILLMProvider> provider,
        bool commitSucceeds,
        Action<AgentTask>? onStatusUpdate)
    {
        var commitFailure = new HttpRequestException("Bad credentials (401)");
        var dequeueCalls = 0;

        var repository = new Mock<IAgentTaskRepository>();

        repository
            .Setup(candidate => candidate.GetNextPendingTaskAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref dequeueCalls) == 1 ? task : null);
        repository
            .Setup(candidate => candidate.UpdateTaskAsync(It.IsAny<AgentTask>(), It.IsAny<CancellationToken>()))
            .Callback<AgentTask, CancellationToken>((updated, _) => onStatusUpdate?.Invoke(updated))
            .Returns(Task.CompletedTask);

        var gitHubService = new Mock<IGitHubService>();

        gitHubService
            .Setup(service => service.GetRepositoryCSharpFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>(StringComparer.Ordinal));
        gitHubService
            .Setup(service => service.CreateBranchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("refs/heads/feat/task-x");

        var commitSetup = gitHubService.Setup(service => service.CommitChangesAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<IReadOnlyDictionary<string, string>>(),
            It.IsAny<CancellationToken>()));

        if (commitSucceeds)
        {
            commitSetup.ReturnsAsync("sha-do-commit");
        }
        else
        {
            // Simula a exceção do Octokit que o GitHubAdapter propaga (logada como erro e não engolida).
            commitSetup.ThrowsAsync(commitFailure);
        }

        gitHubService
            .Setup(service => service.OpenPullRequestAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(PullRequestUrl);

        var taskRouter = new Mock<ITaskRouter>();

        taskRouter
            .Setup(router => router.ResolveProvider(It.IsAny<TaskComplexity>()))
            .Returns(provider.Object);

        // RAG sem vetor: o contexto fica vazio e o teste não depende do índice.
        var embeddingProvider = new Mock<IEmbeddingProvider>();

        embeddingProvider
            .Setup(candidate => candidate.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ReadOnlyMemory<float>.Empty);

        var codeContextRepository = new Mock<ICodeContextRepository>();
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton(repository.Object);
        services.AddSingleton(gitHubService.Object);
        services.AddSingleton(taskRouter.Object);
        services.AddSingleton(embeddingProvider.Object);
        services.AddSingleton(codeContextRepository.Object);
        services.AddSingleton(new CodebaseIndexerService(
            gitHubService.Object,
            codeContextRepository.Object,
            embeddingProvider.Object));
        services.Configure<OrchestratorWorkerOptions>(options =>
        {
            options.PollingIntervalSeconds = 1;
            options.IndexingIntervalMinutes = 60;
            options.QuotaCooldownMinutes = 1;
        });
        services.Configure<FrustrationSettings>(settings => settings.MaxFailures = 3);

        // Medidor de frustração do daemon: Singleton compartilhado entre o laço e a API de revisão —
        // no harness ele é registrado com o mesmo tipo de registro da composição real.
        services.AddSingleton(new FrustrationTracker(limiteMaximo: 3));

        var serviceProvider = services.BuildServiceProvider();
        var logger = new RecordingLogger<LuckeOrchestratorWorker>();

        return new Harness(
            new LuckeOrchestratorWorker(
                serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                serviceProvider.GetRequiredService<FrustrationTracker>(),
                logger),
            logger,
            repository,
            gitHubService,
            commitFailure,
            serviceProvider);
    }
}
