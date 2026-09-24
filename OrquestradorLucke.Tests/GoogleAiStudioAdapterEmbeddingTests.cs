using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OrquestradorLucke.Application.Interfaces;
using OrquestradorLucke.Infrastructure.Adapters;
using OrquestradorLucke.Infrastructure.Configuration;

namespace OrquestradorLucke.Tests;

/// <summary>
/// Contrato da requisição de embedding do RAG (<see cref="GoogleAiStudioAdapter.GenerateEmbeddingAsync"/>):
/// o modelo é o <see cref="ModelCatalog.GeminiEmbedding2"/> e o corpo leva
/// <c>outputDimensionality</c> na raiz, na dimensão da coluna <c>vector(768)</c> — sem esse campo o
/// provedor devolveria 3072 dimensões e a gravação no pgvector falharia.
/// </summary>
/// <remarks>
/// O <see cref="HttpClient"/> aponta para um <see cref="HttpMessageHandler"/> de teste que captura a
/// requisição em vez de sair para a rede: a asserção é sobre o JSON que chega ao Google, não sobre a
/// implementação que o monta.
/// </remarks>
public sealed class GoogleAiStudioAdapterEmbeddingTests
{
    [Fact]
    public async Task GenerateEmbeddingAsync_DeveEnviarOutputDimensionality768NaRaizDoCorpo()
    {
        using var handler = new CapturingHandler("""{ "embedding": { "values": [0.1, 0.2, 0.3] } }""");
        using var adapter = CreateAdapter(handler, ModelCatalog.GeminiEmbedding2);

        var embedding = await adapter.GenerateEmbeddingAsync("class Alvo { }", CancellationToken.None);

        handler.RequestUri.Should().Be(
            new Uri("https://generativelanguage.googleapis.com/v1beta/models/gemini-embedding-2:embedContent"),
            "a rota não pode duplicar o prefixo 'models/' do catálogo");
        handler.ApiKey.Should().Be("chave-de-teste");

        using var body = JsonDocument.Parse(handler.RequestBody);
        var root = body.RootElement;

        root.GetProperty("model").GetString().Should().Be(ModelCatalog.GeminiEmbedding2);
        root.GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString().Should().Be("class Alvo { }");

        // Campo na raiz (e não dentro de 'content') e igual à dimensão da coluna vector(768).
        root.GetProperty("outputDimensionality").GetInt32().Should().Be(768);
        root.GetProperty("outputDimensionality").GetInt32().Should().Be(ModelCatalog.EmbeddingDimensions);

        embedding.ToArray().Should().Equal(0.1f, 0.2f, 0.3f);
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_ModeloSemPrefixo_DeveQualificarNaRotaEmanterNaRaizDoCorpo()
    {
        using var handler = new CapturingHandler("""{ "embedding": { "values": [1] } }""");
        using var adapter = CreateAdapter(handler, "gemini-embedding-2");

        await adapter.GenerateEmbeddingAsync("texto", CancellationToken.None);

        handler.RequestUri.Should().Be(
            new Uri("https://generativelanguage.googleapis.com/v1beta/models/gemini-embedding-2:embedContent"));

        using var body = JsonDocument.Parse(handler.RequestBody);

        // O campo 'model' do corpo exige o identificador qualificado, mesmo com o prefixo omitido no catálogo.
        body.RootElement.GetProperty("model").GetString().Should().Be(ModelCatalog.GeminiEmbedding2);
        body.RootElement.GetProperty("outputDimensionality").GetInt32().Should().Be(ModelCatalog.EmbeddingDimensions);
    }

    /// <summary>
    /// Cria o adapter com o <see cref="HttpClient"/> instrumentado, reaproveitando as mesmas opções da
    /// composição real (BaseUrl da configuração e <see cref="AiStudioOptions.ForModel"/>).
    /// </summary>
    private static GoogleAiStudioAdapter CreateAdapter(HttpMessageHandler handler, string modelName)
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
            Options.Create(options.ForModel(modelName)),
            Mock.Of<IQuotaManager>(),
            NullLogger<GoogleAiStudioAdapter>.Instance);
    }

    /// <summary>Handler de teste: captura a requisição e devolve um envelope <c>embedContent</c> fixo.</summary>
    private sealed class CapturingHandler(string responseBody) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        public string RequestBody { get; private set; } = string.Empty;

        public string? ApiKey { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            ApiKey = request.Headers.TryGetValues("x-goog-api-key", out var values) ? values.Single() : null;
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }
}
