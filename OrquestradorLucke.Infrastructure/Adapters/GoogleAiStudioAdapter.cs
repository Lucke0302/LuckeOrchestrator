using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using OrquestradorLucke.Application.Interfaces;
using OrquestradorLucke.Domain;
using OrquestradorLucke.Infrastructure.Configuration;
using OrquestradorLucke.Infrastructure.Models;

namespace OrquestradorLucke.Infrastructure.Adapters;

/// <summary>
/// Adapter do Google AI Studio (um dos experts do padrão MoE). Envia o prompt no formato
/// <c>generateContent</c>, extrai apenas o artefato útil da resposta — descartando o
/// Chain-of-Thought que os modelos Gemma imprimem antes do payload — e aciona o Circuit Breaker
/// de cota quando o provedor responde 429 (Too Many Requests).
/// </summary>
public sealed class GoogleAiStudioAdapter(
    HttpClient httpClient,
    IOptions<AiStudioOptions> options,
    IQuotaManager quotaManager) : ILLMProvider, IDisposable
{
    private const string ApiKeyHeaderName = "x-goog-api-key";
    private const string ModelsPathSegment = "models/";
    private const string GenerateContentOperation = ":generateContent";

    private readonly AiStudioOptions _options = options.Value;
    private readonly IQuotaManager _quotaManager = quotaManager;

    /// <summary>Identificador do modelo configurado para este adapter.</summary>
    public string ModelName => _options.ModelName;

    public Task<string> AnalyzeContextAsync(string payload, CancellationToken cancellationToken)
        => SendPromptAsync("Analise o contexto abaixo e descreva o plano de execução.", payload, cancellationToken);

    public Task<string> GenerateCodeAsync(string payload, string contextAnalysis, CancellationToken cancellationToken)
        => SendPromptAsync("Gere o código necessário para atender à tarefa.", $"{contextAnalysis}{Environment.NewLine}{payload}", cancellationToken);

    public Task<string> EvaluateErrorAsync(string payload, string generatedCode, string errorMessage, CancellationToken cancellationToken)
        => SendPromptAsync("Explique a causa da falha e proponha a correção.", $"{errorMessage}{Environment.NewLine}{generatedCode}{Environment.NewLine}{payload}", cancellationToken);

    /// <summary>
    /// Descarta o <see cref="HttpClient"/> obtido do <c>IHttpClientFactory</c> (o pool de handlers
    /// permanece compartilhado, portanto não há esgotamento de sockets).
    /// </summary>
    public void Dispose() => httpClient.Dispose();

    /// <summary>Dispara a geração de conteúdo e devolve o artefato saneado da resposta.</summary>
    private async Task<string> SendPromptAsync(string instruction, string content, CancellationToken cancellationToken)
    {
        EnsureConfiguration();

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildRequestUri())
        {
            Content = JsonContent.Create(new
            {
                contents = new[]
                {
                    new { role = "user", parts = new[] { new { text = $"{instruction}{Environment.NewLine}{content}" } } }
                }
            })
        };

        request.Headers.TryAddWithoutValidation(ApiKeyHeaderName, _options.ApiKey);

        using var response = await httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var lockout = TimeSpan.FromHours(Math.Max(1, _options.QuotaLockoutHours));
            var availableAtUtc = DateTimeOffset.UtcNow.Add(lockout);

            // Circuit Breaker de cota: o modelo sai do rodízio MoE até a janela expirar e o roteador
            // cai para o próximo da cadeia. A exceção não deriva de HttpRequestException, portanto
            // a política de retry do Polly a ignora (tentar novamente não recupera cota).
            _quotaManager.LockOutModel(ModelName, lockout);

            throw new QuotaExhaustedException(
                $"Cota do modelo '{ModelName}' esgotada (HTTP 429). Bloqueado por {lockout.TotalHours:0} h; disponível novamente em {availableAtUtc:u}.",
                ModelName,
                availableAtUtc);
        }

        if (IsTransientFailure(response.StatusCode))
        {
            // 5xx/408: HttpRequestException é a falha transitória que o Polly repete com backoff.
            throw new HttpRequestException(
                $"Falha transitória do Google AI Studio ({(int)response.StatusCode} {response.StatusCode}) no modelo '{ModelName}'.",
                inner: null,
                statusCode: response.StatusCode);
        }

        // Demais erros (400/401/403/404 etc.) não são transitórios: falham sem retry.
        response.EnsureSuccessStatusCode();

        var apiResponse = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return ExtractPayload(apiResponse);
    }

    /// <summary>Valida os valores vindos de configuração (IOptions) antes de sair para a rede.</summary>
    private void EnsureConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new InvalidOperationException("AiStudio:ApiKey não configurada. Defina via user-secrets ou variável de ambiente.");
        }

        if (string.IsNullOrWhiteSpace(_options.ApiVersion))
        {
            throw new InvalidOperationException("AiStudio:ApiVersion não configurada (ex.: \"v1beta\").");
        }

        if (string.IsNullOrWhiteSpace(_options.ModelName))
        {
            throw new InvalidOperationException($"{nameof(AiStudioOptions)}.{nameof(AiStudioOptions.ModelName)} não configurado para este adapter.");
        }
    }

    /// <summary>
    /// Monta o endpoint relativo <c>{ApiVersion}/models/{model}:generateContent</c>. Aceita tanto
    /// <c>models/gemini-x</c> quanto <c>gemini-x</c>, evitando duplicar o prefixo <c>models/</c>.
    /// </summary>
    private string BuildRequestUri()
    {
        var model = _options.ModelName.Trim().TrimStart('/');

        if (model.StartsWith(ModelsPathSegment, StringComparison.OrdinalIgnoreCase))
        {
            model = model[ModelsPathSegment.Length..];
        }

        return $"{_options.ApiVersion.Trim().Trim('/')}/{ModelsPathSegment}{model}{GenerateContentOperation}";
    }

    /// <summary>Indica erro transitório (5xx ou 408), que deve ser repetido pela política do Polly.</summary>
    private static bool IsTransientFailure(HttpStatusCode statusCode)
        => (int)statusCode >= 500 || statusCode == HttpStatusCode.RequestTimeout;

    /// <summary>
    /// Lê a string devolvida pelo Google AI Studio e extrai estritamente o bloco JSON ou o código
    /// válido, descartando o raciocínio (Chain-of-Thought) que os modelos Gemma imprimem antes do
    /// payload (ex.: "* Input: ... * Constraint: ...").
    /// </summary>
    /// <returns>
    /// Artefato útil; string vazia quando a resposta não traz payload — caso em que a mecânica de
    /// frustração contabiliza a falha e o roteador escala para o próximo modelo da cadeia.
    /// </returns>
    private static string ExtractPayload(string apiResponse)
    {
        if (string.IsNullOrWhiteSpace(apiResponse))
        {
            return string.Empty;
        }

        if (AiStudioResponseReader.TryReadCandidateText(apiResponse, out var candidateText))
        {
            return AiStudioResponseReader.ExtractStructuredPayload(candidateText);
        }

        // Corpo que não é envelope do AI Studio: só é tratado como saída do modelo quando não é
        // JSON (ex.: proxy devolvendo texto puro). JSON sem "candidates" é erro de protocolo.
        return AiStudioResponseReader.IsJsonDocument(apiResponse)
            ? string.Empty
            : AiStudioResponseReader.ExtractStructuredPayload(apiResponse);
    }
}
