using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrquestradorLucke.Application.Interfaces;
using OrquestradorLucke.Domain;
using OrquestradorLucke.Infrastructure.Configuration;
using OrquestradorLucke.Infrastructure.Models;

namespace OrquestradorLucke.Infrastructure.Adapters;

/// <summary>
/// Adapter do Google AI Studio: um dos experts do padrão MoE (endpoint <c>generateContent</c>) e
/// também o provedor de embeddings do RAG (endpoint <c>embedContent</c>). Extrai apenas o artefato
/// útil da resposta — descartando o Chain-of-Thought que os modelos Gemma imprimem antes do payload
/// — e aciona o Circuit Breaker de cota quando o provedor responde 429 (Too Many Requests).
/// </summary>
/// <remarks>
/// Um único adapter cobre as duas operações porque o tratamento de cota (429), falha transitória
/// (5xx/408 para o retry do Polly) e autenticação é idêntico; o que muda é o endpoint e o
/// identificador de modelo — no RAG o registro aponta para o modelo de embeddings do catálogo.
/// </remarks>
public sealed class GoogleAiStudioAdapter(
    HttpClient httpClient,
    IOptions<AiStudioOptions> options,
    IQuotaManager quotaManager,
    ILogger<GoogleAiStudioAdapter> logger) : ILLMProvider, IEmbeddingProvider, IDisposable
{
    private const string ApiKeyHeaderName = "x-goog-api-key";
    private const string ModelsPathSegment = "models/";
    private const string GenerateContentOperation = ":generateContent";
    private const string EmbedContentOperation = ":embedContent";

    /// <summary>
    /// Tamanho da prévia da resposta do LLM registrada no log. O corpo inteiro é uma string longa e
    /// multilinha (código gerado): o journal do Linux a substitui por <c>[blob data]</c> e o operador
    /// fica sem evidência do que o modelo devolveu.
    /// </summary>
    private const int LogPreviewLength = 200;

    /// <summary>Caracteres de controle (quebras de linha, tabs, formatação) que virariam ruído no log.</summary>
    private static readonly Regex ControlCharacterRegex = new(@"[\p{C}]+", RegexOptions.Compiled);

    /// <summary>Sequências de espaços/indentação colapsadas em um único espaço na prévia do log.</summary>
    private static readonly Regex WhitespaceRunRegex = new(@"\s{2,}", RegexOptions.Compiled);

    /// <summary>
    /// Instrução de geração de código. O contrato de múltiplos arquivos é fixado no próprio prompt
    /// (JSON estrito caminho → conteúdo) porque é o parse dessa resposta que alimenta o commit: o
    /// formato precisa ser previsível, e não inferido a cada resposta.
    /// </summary>
    private const string GenerateCodeInstruction =
        """
        Gere o código necessário para atender à tarefa.
        Responda EXCLUSIVAMENTE com um objeto JSON válido, sem texto antes ou depois, no formato:
        {"caminho/do/arquivo.cs": "conteúdo do código"}
        Cada chave do objeto é o caminho relativo do arquivo dentro do repositório (use "/" como separador) e cada valor é o conteúdo completo daquele arquivo, já com os escapes do JSON.
        Inclua uma chave por arquivo necessário para atender à tarefa.
        """;

    /// <summary>
    /// Instrução de sumarização do pull request. O contrato (JSON estrito com <c>titulo</c> e
    /// <c>descricao</c>, sem cercas de markdown e sem campos extras) é fixado aqui — e não inferido a
    /// cada resposta — porque é o parse dele que preenche o título e o corpo do pull request no host.
    /// </summary>
    private const string PullRequestSummaryInstruction =
        """
        Resuma a tarefa executada nos moldes de um pull request.
        Responda EXCLUSIVAMENTE com um objeto JSON válido, sem texto antes ou depois e sem cercas de markdown, no formato exato:
        {"titulo": "título curto do pull request", "descricao": "corpo do pull request em Markdown"}
        O valor de "titulo" é uma única linha, no padrão Conventional Commits com o escopo da tarefa (ex.: "feat(modulo): implementa o fluxo x").
        O valor de "descricao" é o corpo em Markdown: o resumo da mudança e a lista dos arquivos alterados.
        Não use crases triplas, não inclua comentários e não acrescente nenhum campo além de "titulo" e "descricao".
        """;

    private static readonly JsonSerializerOptions ResponseSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly AiStudioOptions _options = options.Value;
    private readonly IQuotaManager _quotaManager = quotaManager;
    private readonly ILogger<GoogleAiStudioAdapter> _logger = logger;

    /// <summary>Identificador do modelo configurado para este adapter.</summary>
    public string ModelName => _options.ModelName;

    public Task<string> AnalyzeContextAsync(string payload, CancellationToken cancellationToken)
        => SendPromptAsync("Analise o contexto abaixo e descreva o plano de execução.", payload, cancellationToken);

    /// <summary>
    /// Gera os artefatos da tarefa sob o contrato de múltiplos arquivos (JSON estrito) e devolve o
    /// mapa caminho → conteúdo pronto para o commit.
    /// </summary>
    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// Quando o modelo não devolve o JSON de arquivos esperado (ver <see cref="ExtractFileArtifacts"/>).
    /// </exception>
    public async Task<Dictionary<string, string>> GenerateCodeAsync(string payload, string contextAnalysis, CancellationToken cancellationToken)
    {
        EnsureConfiguration();
        ArgumentNullException.ThrowIfNull(payload);

        var modelOutput = await SendPromptAsync(
                GenerateCodeInstruction,
                $"{contextAnalysis}{Environment.NewLine}{payload}",
                cancellationToken)
            .ConfigureAwait(false);

        var artifacts = ExtractFileArtifacts(modelOutput);

        // Auditoria do parse: quantos arquivos sobreviveram à desserialização do JSON do LLM. O número
        // é registrado antes de qualquer efeito no repositório — entrega vazia não passa silenciosa.
        _logger.LogInformation(
            "LLM '{ModelName}': {FileCount} arquivo(s) extraído(s) do JSON de resposta.",
            ModelName,
            artifacts.Count);

        return artifacts;
    }

    /// <summary>
    /// Sumariza a tarefa para o pull request sob o contrato JSON estrito
    /// (<c>{"titulo": "...", "descricao": "..."}</c>) e devolve a saída bruta do modelo.
    /// </summary>
    /// <inheritdoc />
    /// <remarks>
    /// A resposta volta sem tratamento de markdown de propósito: o parse do resumo é do chamador
    /// (worker), que reaproveita a mesma sanitização dos artefatos e aufere o resumo determinístico
    /// quando o modelo foge do contrato — o resumo nunca derruba uma entrega já commitada.
    /// </remarks>
    public async Task<string> GeneratePullRequestSummaryAsync(string payload, string contextAnalysis, CancellationToken cancellationToken)
    {
        EnsureConfiguration();
        ArgumentNullException.ThrowIfNull(payload);

        return await SendPromptAsync(
                PullRequestSummaryInstruction,
                $"{contextAnalysis}{Environment.NewLine}{payload}",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<string> EvaluateErrorAsync(string payload, string generatedCode, string errorMessage, CancellationToken cancellationToken)
        => SendPromptAsync("Explique a causa da falha e proponha a correção.", $"{errorMessage}{Environment.NewLine}{generatedCode}{Environment.NewLine}{payload}", cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Usa o endpoint <c>models/{ModelName}:embedContent</c>. Quando o adapter é registrado como
    /// provedor de embeddings, <see cref="ModelName"/> aponta para o modelo de embeddings do
    /// <c>ModelCatalog</c> — que não participa das cadeias MoE — e um eventual 429 bloqueia apenas
    /// esse modelo no Circuit Breaker de cota, preservando o rodízio dos experts de geração.
    /// O corpo fixa <c>outputDimensionality</c> (ver <see cref="AiStudioOptions.EmbeddingOutputDimensions"/>):
    /// sem esse campo o modelo devolveria 3072 dimensões e o pgvector recusaria a gravação na coluna
    /// <c>vector(768)</c> de <c>code_documents</c>.
    /// </remarks>
    public async Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken)
    {
        EnsureConfiguration();
        ArgumentNullException.ThrowIfNull(text);

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildRequestUri(EmbedContentOperation))
        {
            // O 'outputDimensionality' vai na RAIZ do corpo — é parâmetro do request do embedContent,
            // não do conteúdo embutido — e o modelo trunca o vetor (Matryoshka) para a dimensão
            // contratada pela coluna vector(768). O objeto anônimo é serializado como está (as
            // propriedades já são nomeadas em camelCase, sem naming policy), então o campo sai na API
            // exatamente como "outputDimensionality".
            Content = JsonContent.Create(new
            {
                model = QualifiedModelName,
                content = new
                {
                    parts = new[] { new { text } }
                },
                outputDimensionality = _options.EmbeddingOutputDimensions
            })
        };

        request.Headers.TryAddWithoutValidation(ApiKeyHeaderName, _options.ApiKey);

        var apiResponse = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        return ExtractEmbeddingValues(apiResponse);
    }

    /// <summary>
    /// Descarta o <see cref="HttpClient"/> obtido do <c>IHttpClientFactory</c> (o pool de handlers
    /// permanece compartilhado, portanto não há esgotamento de sockets).
    /// </summary>
    public void Dispose() => httpClient.Dispose();

    /// <summary>Dispara a geração de conteúdo e devolve o artefato saneado da resposta.</summary>
    private async Task<string> SendPromptAsync(string instruction, string content, CancellationToken cancellationToken)
    {
        EnsureConfiguration();

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildRequestUri(GenerateContentOperation))
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

        var apiResponse = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        // Log do LLM em UMA linha e com tamanho limitado: o corpo inteiro (código gerado, com quebras
        // de linha e caracteres de controle) viraria "[blob data]" no journal do Linux e desapareceria
        // do log. O tamanho completo continua registrado para o operador saber o que não foi mostrado.
        _logger.LogInformation(
            "Resposta do LLM '{ModelName}': {Length} caractere(s). Prévia: {Preview}",
            ModelName,
            apiResponse.Length,
            BuildLogPreview(apiResponse));

        return ExtractPayload(apiResponse);
    }

    /// <summary>
    /// Envia a requisição e devolve o corpo da resposta, centralizando o tratamento comum aos
    /// endpoints do Google AI Studio: Circuit Breaker de cota no 429, falha transitória (5xx/408)
    /// para o Polly repetir e erro definitivo nos demais status.
    /// </summary>
    /// <param name="request">Requisição já montada e autenticada com a chave de API.</param>
    /// <param name="cancellationToken">Token de cancelamento da iteração do worker.</param>
    /// <exception cref="QuotaExhaustedException">Quando o provedor responde 429 (cota esgotada).</exception>
    /// <exception cref="HttpRequestException">Quando a falha é transitória (5xx/408) e o Polly deve repetir.</exception>
    private async Task<string> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
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

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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

    /// <summary>Identificador do modelo sem o prefixo <c>models/</c> (aceita as duas formas).</summary>
    private string ModelRouteName
    {
        get
        {
            var model = _options.ModelName.Trim().TrimStart('/');

            return model.StartsWith(ModelsPathSegment, StringComparison.OrdinalIgnoreCase)
                ? model[ModelsPathSegment.Length..]
                : model;
        }
    }

    /// <summary>
    /// Identificador do modelo com o prefixo <c>models/</c> — formato exigido no campo <c>model</c>
    /// do corpo do <c>embedContent</c>.
    /// </summary>
    private string QualifiedModelName => ModelsPathSegment + ModelRouteName;

    /// <summary>
    /// Monta o endpoint relativo <c>{ApiVersion}/models/{model}{operation}</c>. Aceita tanto
    /// <c>models/gemini-x</c> quanto <c>gemini-x</c>, evitando duplicar o prefixo <c>models/</c>.
    /// </summary>
    /// <param name="operation">Operação do endpoint (ex.: <c>:generateContent</c>, <c>:embedContent</c>).</param>
    private string BuildRequestUri(string operation)
        => $"{_options.ApiVersion.Trim().Trim('/')}/{ModelsPathSegment}{ModelRouteName}{operation}";

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

    /// <summary>
    /// Converte o JSON estrito de arquivos — já sem o Chain-of-Thought, pela mesma extração usada em
    /// <see cref="ExtractPayload"/> — no dicionário caminho → conteúdo aceito por
    /// <c>IGitHubService.CommitChangesAsync</c>.
    /// </summary>
    /// <param name="modelOutput">Texto saneado devolvido por <see cref="SendPromptAsync"/>.</param>
    /// <returns>Artefatos por caminho relativo; dicionário vazio quando não houve payload algum.</returns>
    /// <exception cref="InvalidOperationException">
    /// Quando há payload mas ele não é o objeto JSON de arquivos (chave string → valor string), não
    /// traz nenhum arquivo ou não traz nenhum caminho utilizável no repositório. A exceção é a
    /// interface da falha para a mecânica de frustração: o worker contabiliza a tentativa e escala o
    /// circuito para o modelo mais robusto no limite.
    /// </exception>
    private static Dictionary<string, string> ExtractFileArtifacts(string modelOutput)
    {
        if (string.IsNullOrWhiteSpace(modelOutput))
        {
            // Sem payload não há o que parsear: o dicionário vazio sinaliza "retorno vazio" ao worker.
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        // Modelos entregam o objeto cercado por crases de markdown (```json ... ```): a sanitização
        // remove a marcação antes de o JsonSerializer receber a string — sem ela o parse falharia e a
        // tarefa seguiria como "resposta não parseável" mesmo com o JSON correto dentro do bloco.
        var sanitized = AiStudioResponseReader.SanitizeJsonPayload(modelOutput);

        Dictionary<string, string>? files;

        try
        {
            files = JsonSerializer.Deserialize<Dictionary<string, string>>(sanitized, ResponseSerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"O modelo não devolveu o JSON estrito de arquivos esperado ({{\"caminho/do/arquivo.cs\": \"conteúdo\"}}). Prévia da resposta: {BuildLogPreview(sanitized)}",
                ex);
        }

        if (files is null || files.Count == 0)
        {
            // Erro explícito (e não retorno vazio): a tarefa precisa cair no tratamento de falha do
            // worker com o motivo no log, em vez de seguir para o commit sem nada a versionar.
            throw new InvalidOperationException(
                $"O JSON devolvido pelo modelo não contém nenhum arquivo. Prévia da resposta: {BuildLogPreview(sanitized)}");
        }

        var artifacts = new Dictionary<string, string>(files.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var path = NormalizeRepositoryPath(file.Key);

            if (path.Length == 0)
            {
                // Chave em branco não é um caminho publicável no repositório.
                continue;
            }

            artifacts[path] = file.Value;
        }

        if (artifacts.Count == 0)
        {
            // JSON desserializado, porém sem nenhum caminho publicável: falha explícita — o retorno
            // vazio silencioso é o que deixava a branch criada sem commit e o PR sem abrir.
            throw new InvalidOperationException(
                $"Nenhum dos {files.Count} caminho(s) devolvido(s) pelo modelo é utilizável no repositório. Prévia da resposta: {BuildLogPreview(sanitized)}");
        }

        return artifacts;
    }

    /// <summary>
    /// Prepara um trecho de texto para log em uma única linha: colapsa quebras de linha e caracteres
    /// de controle, compacta a indentação e limita o tamanho. Sem isso, o corpo gigante devolvido pelo
    /// LLM é omitido pelo journal do Linux (<c>[blob data]</c>) e o debug fica sem a evidência do
    /// payload recebido.
    /// </summary>
    /// <param name="text">Texto a resumir (resposta bruta do modelo ou payload já sanitizado).</param>
    /// <param name="maxLength">Tamanho máximo da prévia (default <see cref="LogPreviewLength"/>).</param>
    /// <returns>Prévia de uma linha; string vazia quando não há texto.</returns>
    private static string BuildLogPreview(string text, int maxLength = LogPreviewLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var flattened = ControlCharacterRegex.Replace(text, " ");
        flattened = WhitespaceRunRegex.Replace(flattened, " ").Trim();

        return flattened.Length <= maxLength
            ? flattened
            : flattened[..maxLength] + "...";
    }

    /// <summary>
    /// Normaliza o caminho devolvido pelo modelo para o formato aceito pelo Git: separador
    /// <c>/</c>, sem espaços nas pontas e sem barra inicial (caminho absoluto não é versionável).
    /// </summary>
    private static string NormalizeRepositoryPath(string path)
        => path.Trim().Replace('\\', '/').TrimStart('/');

    /// <summary>
    /// Lê o envelope do <c>embedContent</c> e devolve os valores do vetor.
    /// </summary>
    /// <param name="apiResponse">Corpo bruto devolvido pelo provedor.</param>
    /// <returns>
    /// Vetor do modelo; <see cref="ReadOnlyMemory{T}.Empty"/> quando o corpo não é o envelope
    /// esperado ou não traz <c>embedding.values</c> — o chamador decide se descarta o documento.
    /// </returns>
    private static ReadOnlyMemory<float> ExtractEmbeddingValues(string apiResponse)
    {
        if (string.IsNullOrWhiteSpace(apiResponse))
        {
            return ReadOnlyMemory<float>.Empty;
        }

        EmbeddingResponse? envelope;

        try
        {
            envelope = JsonSerializer.Deserialize<EmbeddingResponse>(apiResponse, ResponseSerializerOptions);
        }
        catch (JsonException)
        {
            return ReadOnlyMemory<float>.Empty;
        }

        var values = envelope?.Embedding?.Values;

        return values is { Count: > 0 }
            ? values.ToArray()
            : ReadOnlyMemory<float>.Empty;
    }
}
