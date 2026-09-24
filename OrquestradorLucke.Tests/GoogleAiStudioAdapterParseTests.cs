using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using OrquestradorLucke.Application.Interfaces;
using OrquestradorLucke.Infrastructure.Adapters;
using OrquestradorLucke.Infrastructure.Configuration;

namespace OrquestradorLucke.Tests;

/// <summary>
/// Parse da resposta do LLM no <see cref="GoogleAiStudioAdapter"/>: o JSON cercado por crases de
/// markdown é sanitizado antes do <see cref="JsonSerializer"/>, a contagem de arquivos extraídos vai
/// para o log e o retorno sem nenhum arquivo utilizável falha de forma explícita (em vez de seguir
/// para o commit em branco — o sintoma da branch criada sem nada versionado).
/// </summary>
public sealed class GoogleAiStudioAdapterParseTests
{
    private const string FilePath = "src/Alvo.cs";
    private const string FileContent = "namespace Alvo; public class Alvo { }";

    /// <summary>Formas como os modelos entregam o mesmo JSON de arquivos.</summary>
    public static TheoryData<string> ModelPayloads => new()
    {
        // JSON puro (contrato ideal).
        $$"""{"{{FilePath}}": "{{FileContent}}"}""",
        // Bloco de markdown com rótulo na cerca.
        $"```json\n{{\"{FilePath}\": \"{FileContent}\"}}\n```",
        // Crases coladas no objeto, na mesma linha.
        $"```json{{\"{FilePath}\": \"{FileContent}\"}}```",
        // Rótulo 'json' solto, sem cerca alguma.
        $"json\n{{\"{FilePath}\": \"{FileContent}\"}}",
        // Cerca sem rótulo e com prosa dentro do bloco.
        $"```\nClaro! Segue o arquivo:\n{{\"{FilePath}\": \"{FileContent}\"}}\n```",
        // Cerca rotulada com prosa antes do objeto (a sanitização extrai o trecho balanceado).
        $"```json\nClaro! Segue o arquivo:\n{{\"{FilePath}\": \"{FileContent}\"}}\n```"
    };

    [Theory]
    [MemberData(nameof(ModelPayloads))]
    public async Task GenerateCodeAsync_DeveSanitizarJsonDoLlmAntesDeDesserializar(string modelPayload)
    {
        using var handler = new StubHandler(BuildEnvelope(modelPayload));
        using var adapter = CreateAdapter(handler, new RecordingLogger<GoogleAiStudioAdapter>());

        var artifacts = await adapter.GenerateCodeAsync("crie a classe Alvo", string.Empty, CancellationToken.None);

        artifacts.Should().ContainKey(FilePath).WhoseValue.Should().Be(FileContent);
    }

    [Fact]
    public async Task GenerateCodeAsync_CrasesDentroDeStringDoJson_NaoDevemSerMutadas()
    {
        // Uma string do JSON pode conter crases legítimas (markdown no arquivo gerado): a sanitização
        // só remove cercas quando o texto não é JSON válido, então o conteúdo chega intacto ao commit.
        const string contentWithBackticks = "```json exemplo de markdown ```";

        var payload = $"{{\"{FilePath}\": {JsonSerializer.Serialize(contentWithBackticks)}}}";

        using var handler = new StubHandler(BuildEnvelope(payload));
        using var adapter = CreateAdapter(handler, new RecordingLogger<GoogleAiStudioAdapter>());

        var artifacts = await adapter.GenerateCodeAsync("crie a classe Alvo", string.Empty, CancellationToken.None);

        artifacts[FilePath].Should().Be(contentWithBackticks);
    }

    [Theory]
    [InlineData("{}", "não contém nenhum arquivo")]
    [InlineData("""{"": "conteudo"}""", "utilizável")]
    public async Task GenerateCodeAsync_RespostaSemArquivoUtilizavel_DeveFalharExplicitamente(
        string modelPayload,
        string expectedMessageFragment)
    {
        using var handler = new StubHandler(BuildEnvelope(modelPayload));
        using var adapter = CreateAdapter(handler, new RecordingLogger<GoogleAiStudioAdapter>());

        var act = async () => await adapter.GenerateCodeAsync("crie a classe Alvo", string.Empty, CancellationToken.None);

        // Erro explícito = a tarefa cai no tratamento de falha do worker (frustração → overdrive → Falhou).
        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{expectedMessageFragment}*");
    }

    [Fact]
    public async Task GenerateCodeAsync_DeveLogarContagemDeArquivosEPreviaDaRespostaEmUmaLinha()
    {
        var logger = new RecordingLogger<GoogleAiStudioAdapter>();

        // Corpo pretty-printed (com quebras de linha reais) e longo: a prévia precisa sair em UMA linha
        // e truncada — é o que impede o journal do Linux de substituir a resposta por "[blob data]".
        var longContent = string.Join(Environment.NewLine, Enumerable.Repeat("    public void Metodo() { }", 40));
        var payload = $"```json\n{{\"{FilePath}\": {JsonSerializer.Serialize(longContent)}}}\n```";
        var prettyEnvelope = JsonSerializer.Serialize(
            JsonDocument.Parse(BuildEnvelope(payload)).RootElement,
            new JsonSerializerOptions { WriteIndented = true });

        using var handler = new StubHandler(prettyEnvelope);
        using var adapter = CreateAdapter(handler, logger);

        var artifacts = await adapter.GenerateCodeAsync("crie a classe Alvo", string.Empty, CancellationToken.None);

        artifacts.Should().ContainKey(FilePath);

        // Auditoria do parse: o número de arquivos extraídos do JSON fica registrado.
        logger.Messages
            .Should()
            .Contain(message => message.Contains("1 arquivo(s) extraído(s) do JSON de resposta"));

        var previews = logger.Messages.Where(message => message.Contains("Prévia:")).ToList();

        previews.Should().HaveCount(1);

        var preview = previews[0];

        preview.Should().NotContain("\n").And.NotContain("\r");
        preview.Should().Contain("caractere(s)");
        preview.Should().EndWith("...", "a prévia é truncada em 200 caracteres");
        preview.Length.Should().BeLessThan(400);
    }

    /// <summary>Envelope do AI Studio com o texto do candidato informado (uma parte, sem thinking).</summary>
    private static string BuildEnvelope(string candidateText)
        => $"{{\"candidates\":[{{\"content\":{{\"role\":\"model\",\"parts\":[{{\"text\":{JsonSerializer.Serialize(candidateText)}}}]}}}}]}}";

    /// <summary>Cria o adapter com o handler de teste e o logger informado (mesmas opções da composição).</summary>
    private static GoogleAiStudioAdapter CreateAdapter(HttpMessageHandler handler, ILogger<GoogleAiStudioAdapter> logger)
    {
        var options = new AiStudioOptions
        {
            BaseUrl = "https://generativelanguage.googleapis.com/",
            ApiKey = "chave-de-teste",
            ApiVersion = "v1beta"
        };

        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(options.BaseUrl)
        };

        return new GoogleAiStudioAdapter(
            httpClient,
            Options.Create(options.ForModel(ModelCatalog.Gemma4_26bA4bIt)),
            Mock.Of<IQuotaManager>(),
            logger);
    }

    /// <summary>Handler de teste: devolve o corpo informado sem sair para a rede.</summary>
    private sealed class StubHandler(string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
    }

}