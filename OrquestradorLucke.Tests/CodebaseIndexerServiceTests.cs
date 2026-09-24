using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Moq;
using OrquestradorLucke.Application.Interfaces;
using OrquestradorLucke.Application.Services;
using OrquestradorLucke.Domain;

namespace OrquestradorLucke.Tests;

/// <summary>
/// Comportamento do indexador do RAG (<see cref="CodebaseIndexerService"/>): só arquivos novos ou
/// alterados consomem cota de embedding e, ao fim de cada sincronização, o índice é limpo dos
/// documentos que não existem mais na árvore da branch base.
/// </summary>
public sealed class CodebaseIndexerServiceTests
{
    [Fact]
    public async Task IndexChangedDocumentsAsync_DeveRemoverOrfaosUsandoOsCaminhosDaArvoreAtual()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["src/Novo.cs"] = "class Novo { }",
            ["src/Inalterado.cs"] = "class Inalterado { }"
        };

        var repository = new Mock<ICodeContextRepository>();
        repository
            .Setup(candidate => candidate.GetAllTrackedFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(["src/Inalterado.cs", "src/Removido.cs"]);
        repository
            .Setup(candidate => candidate.GetTrackedContentHashesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["src/Inalterado.cs"] = ComputeContentHash("class Inalterado { }"),
                ["src/Removido.cs"] = "hash-antigo"
            });

        IEnumerable<string>? activePaths = null;

        repository
            .Setup(candidate => candidate.DeleteOrphanDocumentsAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, CancellationToken>((paths, _) => activePaths = paths)
            .Returns(Task.CompletedTask);

        var (service, embeddingProvider) = CreateService(files, repository);

        var result = await service.IndexChangedDocumentsAsync(CancellationToken.None);

        // Apenas o arquivo novo foi embutido; o inalterado saiu pelo hash e o removido pela faxina.
        result.Should().Be(new CodebaseIndexingResult(files.Count, 1, 0));
        activePaths.Should().BeEquivalentTo("src/Novo.cs", "src/Inalterado.cs");

        embeddingProvider.Verify(
            provider => provider.GenerateEmbeddingAsync("class Novo { }", It.IsAny<CancellationToken>()),
            Times.Once);

        repository.Verify(
            candidate => candidate.UpsertDocumentAsync(
                It.Is<CodeDocument>(document => document.FilePath == "src/Novo.cs"),
                It.IsAny<CancellationToken>()),
            Times.Once);

        repository.Verify(
            candidate => candidate.DeleteOrphanDocumentsAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task IndexChangedDocumentsAsync_ArquivoInalterado_NaoDeveConsumirEmbedding()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["src/Inalterado.cs"] = "class Inalterado { }"
        };

        var repository = new Mock<ICodeContextRepository>();
        repository
            .Setup(candidate => candidate.GetAllTrackedFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(["src/Inalterado.cs"]);
        repository
            .Setup(candidate => candidate.GetTrackedContentHashesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["src/Inalterado.cs"] = ComputeContentHash("class Inalterado { }")
            });
        repository
            .Setup(candidate => candidate.DeleteOrphanDocumentsAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var (service, embeddingProvider) = CreateService(files, repository);

        var result = await service.IndexChangedDocumentsAsync(CancellationToken.None);

        result.Should().Be(new CodebaseIndexingResult(1, 0, 0));

        embeddingProvider.Verify(
            provider => provider.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task IndexChangedDocumentsAsync_SemArquivosNoRepositorio_NaoDeveApagarOIndice()
    {
        var repository = new Mock<ICodeContextRepository>();

        var (service, _) = CreateService([], repository);

        var result = await service.IndexChangedDocumentsAsync(CancellationToken.None);

        result.Should().Be(new CodebaseIndexingResult(0, 0, 0));

        // Árvore vazia (leitura degradada do GitHub) não pode ser interpretada como "todos os
        // arquivos foram removidos": a faxina só roda sobre uma árvore verificada.
        repository.Verify(
            candidate => candidate.DeleteOrphanDocumentsAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task IndexChangedDocumentsAsync_DocumentoSemEmbedding_DeveSerDescartadoENaoEscrito()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["src/Novo.cs"] = "class Novo { }"
        };

        var repository = new Mock<ICodeContextRepository>();
        repository
            .Setup(candidate => candidate.GetAllTrackedFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repository
            .Setup(candidate => candidate.GetTrackedContentHashesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repository
            .Setup(candidate => candidate.DeleteOrphanDocumentsAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var gitHubService = new Mock<IGitHubService>();
        gitHubService
            .Setup(service => service.GetRepositoryCSharpFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        var embeddingProvider = new Mock<IEmbeddingProvider>();
        embeddingProvider
            .Setup(provider => provider.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ReadOnlyMemory<float>.Empty);

        var service = new CodebaseIndexerService(gitHubService.Object, repository.Object, embeddingProvider.Object);

        var result = await service.IndexChangedDocumentsAsync(CancellationToken.None);

        result.Should().Be(new CodebaseIndexingResult(1, 0, 1));

        repository.Verify(
            candidate => candidate.UpsertDocumentAsync(It.IsAny<CodeDocument>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>Cria o indexador com a árvore informada e devolve o mock do provedor de embeddings.</summary>
    private static (CodebaseIndexerService Service, Mock<IEmbeddingProvider> EmbeddingProvider) CreateService(
        Dictionary<string, string> files,
        Mock<ICodeContextRepository> codeContextRepository)
    {
        var gitHubService = new Mock<IGitHubService>();
        gitHubService
            .Setup(service => service.GetRepositoryCSharpFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        var embeddingProvider = new Mock<IEmbeddingProvider>();
        embeddingProvider
            .Setup(provider => provider.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReadOnlyMemory<float>(new[] { 0.1f, 0.2f }));

        return (
            new CodebaseIndexerService(gitHubService.Object, codeContextRepository.Object, embeddingProvider.Object),
            embeddingProvider);
    }

    /// <summary>SHA-256 hexadecimal do conteúdo — o mesmo hash calculado pelo indexador.</summary>
    private static string ComputeContentHash(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
