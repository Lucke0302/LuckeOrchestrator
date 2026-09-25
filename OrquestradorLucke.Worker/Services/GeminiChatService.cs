using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using OrquestradorLucke.Infrastructure.Configuration;

namespace OrquestradorLucke.Worker.Services;

/// <summary>
/// Motor de chat do painel: envia a mensagem do operador ao endpoint <c>generateContent</c> do Google
/// AI Studio e força o modelo a separar raciocínio e resposta final pelo delimitador
/// <c>&lt;FIM_DO_RACIOCINIO&gt;</c> — o C# converte isso nas tags <c>&lt;think&gt;</c> que o painel
/// isola, mostrando a resposta final fora delas.
/// </summary>
/// <remarks>
/// <para>
/// A resiliência é do próprio motor (o typed client não recebe a política Polly dos experts MoE): até
/// <see cref="MaxAttempts"/> tentativas, com o mesmo expert nas primeiras e um modelo mais robusto
/// como fallback na última. Falha de rede, status HTTP fora da faixa 2xx ou resposta sem texto caem
/// no mesmo laço — o erro vai para o log, a espera é de <see cref="RetryDelay"/> e a chamada é
/// repetida.
/// </para>
/// <para>
/// Nem a URL nem a chave estão no código: <c>Gemini:BaseUrl</c>, <c>Gemini:ApiVersion</c> e
/// <c>Gemini:ApiKey</c> vêm da configuração (appsettings/user-secrets/variável de ambiente), e os
/// identificadores de modelo saem do <see cref="ModelCatalog"/> — a fonte única de verdade dos modelos
/// do projeto.
/// </para>
/// </remarks>
public sealed class GeminiChatService(
    HttpClient httpClient,
    IConfiguration configuration,
    ILogger<GeminiChatService> logger)
{
    /// <summary>Seção de configuração do motor de chat.</summary>
    private const string SectionName = "Gemini";

    /// <summary>Chave da API do Google AI Studio (segredo: user-secrets/variável de ambiente).</summary>
    private const string ApiKeyConfigurationKey = $"{SectionName}:ApiKey";

    /// <summary>URL base do Google AI Studio (usada no start da URL absoluta do endpoint).</summary>
    private const string BaseUrlConfigurationKey = $"{SectionName}:BaseUrl";

    /// <summary>Versão da API de geração de conteúdo (segmento inicial do caminho do endpoint).</summary>
    private const string ApiVersionConfigurationKey = $"{SectionName}:ApiVersion";

    /// <summary>
    /// Segmento de caminho do modelo: o <see cref="ModelCatalog"/> guarda o identificador já com ele,
    /// então é removido para compor a rota e reposto na montagem da URL.
    /// </summary>
    private const string ModelsPathSegment = "models/";

    /// <summary>Operação do endpoint de geração de conteúdo.</summary>
    private const string GenerateContentOperation = ":generateContent";

    /// <summary>Nome do parâmetro de query que carrega a chave da API.</summary>
    private const string ApiKeyQueryParameterName = "key";

    /// <summary>
    /// Teto de tentativas: as primeiras usam o expert de entrada do catálogo e a última faz fallback
    /// para o modelo mais robusto.
    /// </summary>
    private const int MaxAttempts = 3;

    /// <summary>Espera entre tentativas, para não martelar o provedor com a mesma falha.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Instrução de sistema que fixa o contrato de resposta baseado no delimitador
    /// <c>&lt;FIM_DO_RACIOCINIO&gt;</c>: o raciocínio vem em bullet points e a resposta final limpa vem
    /// logo abaixo da tag. O C# converte isso nas tags de pensamento do painel, sem depender de o
    /// modelo emitir XML ou JSON.
    /// </summary>
    private const string ChainOfThoughtInstruction =
        "Você é um assistente prestativo. Faça todo o seu raciocínio natural em bullet points. Ao terminar, escreva OBRIGATORIAMENTE a tag exata <FIM_DO_RACIOCINIO> e, logo abaixo dela, a sua resposta final limpa para o usuário em português, sem bullets e sem aspas.";

    private readonly IConfiguration _configuration = configuration;
    private readonly ILogger<GeminiChatService> _logger = logger;

    /// <summary>
    /// Envia a mensagem do operador e devolve a resposta do modelo com a nota de persistência
    /// (<c>*(Gerado usando {modelo} em {tentativas} tentativa(s))*</c>) anexada no fim.
    /// </summary>
    /// <param name="prompt">Mensagem do operador (o raciocínio volta embrulhado nas tags de pensamento).</param>
    /// <param name="cancellationToken">Token de cancelamento da requisição HTTP do painel.</param>
    /// <returns>Texto do modelo concatenado com a nota de tentativas.</returns>
    /// <exception cref="ArgumentException">Quando <paramref name="prompt"/> é nulo, vazio ou apenas espaços.</exception>
    /// <exception cref="InvalidOperationException">
    /// Quando a configuração do motor está incompleta, quando o modelo responde sem texto útil ou
    /// quando as <see cref="MaxAttempts"/> tentativas se esgotam.
    /// </exception>
    public async Task<string> SendMessageAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        EnsureConfiguration();

        Exception? lastFailure = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            // Tentativas 1 e 2 no expert de entrada; a última escalona para o fallback do catálogo.
            var modelName = attempt < MaxAttempts ? ModelCatalog.Gemma4_26bA4bIt : ModelCatalog.Gemma4_31bIt;

            try
            {
                var reply = await GenerateContentAsync(modelName, prompt, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "Chat do Gemini respondido por '{ModelName}' na tentativa {Attempt}/{MaxAttempts}.",
                    ModelIdentifier(modelName),
                    attempt,
                    MaxAttempts);

                return $"{reply}\n\n*(Gerado usando {ModelIdentifier(modelName)} em {attempt} tentativa(s))*";
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Cancelamento não é falha do provedor: propaga direto, sem consumir tentativa.
                lastFailure = ex;

                _logger.LogError(
                    ex,
                    "Chat do Gemini falhou na tentativa {Attempt}/{MaxAttempts} com o modelo '{ModelName}'.",
                    attempt,
                    MaxAttempts,
                    ModelIdentifier(modelName));

                if (attempt < MaxAttempts)
                {
                    await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        throw new InvalidOperationException(
            $"O chat do Gemini não respondeu após {MaxAttempts} tentativas.",
            lastFailure);
    }

    /// <summary>Dispara o <c>generateContent</c> do modelo e devolve o texto final já no contrato de tags do painel.</summary>
    private async Task<string> GenerateContentAsync(string modelName, string prompt, CancellationToken cancellationToken)
    {
        // Reforço do contrato no fim da fala do operador: alguns modelos (ex.: Gemma) ignoram a
        // system_instruction, então a mesma exigência é colada no próprio prompt — é a última coisa
        // lida antes da geração, o que aumenta muito a aderência ao delimitador.
        var enhancedPrompt = $"{prompt}\n\n[MANDATORY INSTRUCTION: 1. Faça todo o seu raciocínio natural em bullet points. 2. Ao terminar, escreva OBRIGATORIAMENTE a tag exata: <FIM_DO_RACIOCINIO> 3. Logo abaixo da tag, escreva a sua resposta final limpa para o usuário.]";

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpointUri(modelName))
        {
            // A instrução de sistema fixa o contrato do delimitador de raciocínio e o 'contents' carrega a fala
            // já reforçada do operador. As propriedades saem nomeadas exatamente como a API espera
            // (system_instruction), sem depender de naming policy.
            Content = JsonContent.Create(new
            {
                system_instruction = new { parts = new[] { new { text = ChainOfThoughtInstruction } } },
                contents = new[] { new { parts = new[] { new { text = enhancedPrompt } } } }
            })
        };

        using var response = await httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        // Qualquer status fora da faixa 2xx derruba a tentativa: o laço loga, espera e repete (até
        // escalar para o fallback na última).
        response.EnsureSuccessStatusCode();

        var rawResponse = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return ExtractReplyText(rawResponse);
    }

    /// <summary>
    /// Lê <c>candidates[0].content.parts[0].text</c> e divide a resposta do modelo pelo delimitador
    /// <c>&lt;FIM_DO_RACIOCINIO&gt;</c>: o que vem antes é o raciocínio (bullet points) e o que vem
    /// depois é a resposta final já limpa. O resultado sai no contrato de tags do painel
    /// (<c>&lt;think&gt;{thoughts}&lt;/think&gt;</c> seguido da resposta). Texto ausente continua sendo
    /// falha — a última tentativa escala para o fallback.
    /// </summary>
    /// <exception cref="InvalidOperationException">Quando o envelope não traz texto útil.</exception>
    private static string ExtractReplyText(string rawResponse)
    {
        var envelope = JsonSerializer.Deserialize<ChatCompletionEnvelope>(rawResponse);

        var rawText = envelope?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text?.Trim();

        if (string.IsNullOrWhiteSpace(rawText))
        {
            throw new InvalidOperationException(
                "O Gemini respondeu sem texto em 'candidates[0].content.parts[0].text'.");
        }

        string thoughts;
        string answer;
        var delimiter = "<FIM_DO_RACIOCINIO>";

        if (rawText.Contains(delimiter))
        {
            var parts = rawText.Split(delimiter, StringSplitOptions.None);
            thoughts = parts[0].Trim();

            // Limpeza agressiva do início/fim da resposta para remover bullets (*, -) e aspas que o Gemma injeta.
            answer = parts[1].TrimStart('*', '-', ' ').Trim('"', '\'').Trim();
        }
        else
        {
            thoughts = "O modelo processou a resposta diretamente.";
            answer = rawText;
        }

        var finalParsedText = $"<think>\n{thoughts}\n</think>\n\n{answer}";

        return finalParsedText;
    }

    /// <summary>
    /// Monta a URL absoluta do endpoint:
    /// <c>{BaseUrl}/{ApiVersion}/models/{modelo}:generateContent?key={apiKey}</c>.
    /// </summary>
    private string BuildEndpointUri(string modelName)
    {
        var baseUrl = _configuration[BaseUrlConfigurationKey]!.Trim().TrimEnd('/');
        var apiVersion = _configuration[ApiVersionConfigurationKey]!.Trim().Trim('/');
        var apiKey = Uri.EscapeDataString(_configuration[ApiKeyConfigurationKey]!.Trim());
        var model = ModelIdentifier(modelName);

        return $"{baseUrl}/{apiVersion}/{ModelsPathSegment}{model}{GenerateContentOperation}" +
            $"?{ApiKeyQueryParameterName}={apiKey}";
    }

    /// <summary>Valida os valores de configuração antes de sair para a rede.</summary>
    /// <exception cref="InvalidOperationException">Quando uma chave obrigatória de <c>Gemini</c> está ausente.</exception>
    private void EnsureConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_configuration[ApiKeyConfigurationKey]))
        {
            throw new InvalidOperationException(
                $"{ApiKeyConfigurationKey} não configurada. Defina via user-secrets ou variável de ambiente (Gemini__ApiKey).");
        }

        if (string.IsNullOrWhiteSpace(_configuration[BaseUrlConfigurationKey]))
        {
            throw new InvalidOperationException(
                $"{BaseUrlConfigurationKey} não configurada (ex.: \"https://generativelanguage.googleapis.com/\").");
        }

        if (string.IsNullOrWhiteSpace(_configuration[ApiVersionConfigurationKey]))
        {
            throw new InvalidOperationException(
                $"{ApiVersionConfigurationKey} não configurada (ex.: \"v1beta\").");
        }
    }

    /// <summary>
    /// Identificador do modelo sem o prefixo <c>models/</c>: é a forma usada na rota (o segmento é
    /// reposto em <see cref="BuildEndpointUri"/>) e a exibida na nota de persistência.
    /// </summary>
    private static string ModelIdentifier(string modelName)
    {
        var identifier = modelName.Trim().TrimStart('/');

        return identifier.StartsWith(ModelsPathSegment, StringComparison.OrdinalIgnoreCase)
            ? identifier[ModelsPathSegment.Length..]
            : identifier;
    }

    /// <summary>Envelope de resposta do <c>generateContent</c> — apenas o que o chat consome.</summary>
    private sealed record ChatCompletionEnvelope
    {
        [JsonPropertyName("candidates")]
        public IReadOnlyList<ChatCandidate>? Candidates { get; init; }
    }

    /// <summary>Candidato de resposta do modelo.</summary>
    private sealed record ChatCandidate
    {
        [JsonPropertyName("content")]
        public ChatContent? Content { get; init; }
    }

    /// <summary>Conteúdo do candidato: os fragmentos de texto produzidos.</summary>
    private sealed record ChatContent
    {
        [JsonPropertyName("parts")]
        public IReadOnlyList<ChatPart>? Parts { get; init; }
    }

    /// <summary>Fragmento de texto do conteúdo.</summary>
    private sealed record ChatPart
    {
        [JsonPropertyName("text")]
        public string? Text { get; init; }
    }
}
