using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrquestradorLucke.Infrastructure.Models;

/// <summary>
/// Leitura defensiva das respostas do Google AI Studio em duas etapas:
/// <list type="number">
/// <item>desserializa o envelope e concatena apenas os fragmentos de resposta (ignorando <c>thought</c>);</item>
/// <item>extrai estritamente o artefato útil (bloco JSON ou código), descartando o Chain-of-Thought
/// que os modelos Gemma imprimem antes do payload (ex.: "* Input: ... * Constraint: ...").</item>
/// </list>
/// </summary>
internal static class AiStudioResponseReader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Bloco cercado por crases triplas, opcionalmente rotulado com a linguagem na primeira linha.</summary>
    private static readonly Regex FencedBlockRegex = new(
        @"```(?<info>[^\r\n`]*)\r?\n(?<content>.*?)(?:```|\z)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>Blocos explícitos de raciocínio (tags de thinking/scratchpad).</summary>
    private static readonly Regex ReasoningBlockRegex = new(
        @"<(?:thinking|thought|reasoning|scratchpad)\b[^>]*>.*?</(?:thinking|thought|reasoning|scratchpad)>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>Linhas de raciocínio ("* Input:", "- Constraint:", "1. Thinking:" etc.) emitidas antes do payload.</summary>
    private static readonly Regex ReasoningLineRegex = new(
        @"^[ \t]*(?:[*•\-]|\d+[.)])[ \t]*(?:inputs?|constraints?|tasks?|contexts?|contextos?|thinkings?|thoughts?|reasonings?|racioc\w*|plans?|planos?|steps?|passos?)\b.*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>Tokens de controle do chat template (Gemma) que não fazem parte do artefato.</summary>
    private static readonly Regex ControlTokenRegex = new(
        @"</?(?:start_of_turn|end_of_turn|eos|bos)>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Cerca de bloco markdown (crases ou tils) ocupando a linha inteira, com rótulo de linguagem
    /// opcional — a forma como os modelos entregam o JSON: <c>```json</c> na primeira linha e
    /// <c>```</c> na última.
    /// </summary>
    private static readonly Regex FenceLineRegex = new(
        "^[ \t]*(?:`{3,}|~{3,})[ \t]*(?<label>[A-Za-z0-9_+.#-]*)[ \t]*$\r?\n?",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>Cerca remanescente colada ao payload, na mesma linha (ex.: <c>```json{"a":1}```</c>).</summary>
    private static readonly Regex InlineFenceRegex = new(
        @"(?:`{3,}|~{3,})",
        RegexOptions.Compiled);

    /// <summary>Rótulo <c>json</c>/<c>jsonc</c> colado no objeto, sem cerca alguma (ex.: <c>json {...}</c>).</summary>
    private static readonly Regex LeadingJsonLabelRegex = new(
        @"^\s*json\w*\s*:?\s*(?=[\{\[])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Lê o envelope do AI Studio e devolve o texto produzido pelos candidatos.</summary>
    /// <param name="rawApiResponse">Corpo bruto devolvido pelo provedor.</param>
    /// <param name="modelOutput">
    /// Texto concatenado dos fragmentos de resposta (sem os marcados como <c>thought</c>). Vazio
    /// quando o envelope é válido mas não traz texto útil (ex.: <c>SAFETY</c>/<c>MAX_TOKENS</c>).
    /// </param>
    /// <returns><c>false</c> quando o corpo não é um envelope reconhecível (ex.: texto puro).</returns>
    public static bool TryReadCandidateText(string rawApiResponse, out string modelOutput)
    {
        modelOutput = string.Empty;

        if (string.IsNullOrWhiteSpace(rawApiResponse))
        {
            return false;
        }

        AiStudioResponse? envelope;

        try
        {
            envelope = JsonSerializer.Deserialize<AiStudioResponse>(rawApiResponse, SerializerOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        var candidates = envelope?.Candidates;

        if (candidates is null || candidates.Count == 0)
        {
            return false;
        }

        foreach (var candidate in candidates)
        {
            var text = ReadCandidateText(candidate);

            if (!string.IsNullOrWhiteSpace(text))
            {
                modelOutput = text;
                return true;
            }
        }

        // Envelope válido, porém sem texto útil: mantém vazio para a mecânica de frustração
        // contabilizar a falha (retorno vazio) em vez de tratar o envelope como payload.
        return true;
    }
    /// <summary>
    /// Extrai estritamente o artefato entregável do texto do modelo, descartando o raciocínio.
    /// Ordem de preferência: bloco cercado, JSON balanceado, texto remanescente sem as linhas de
    /// raciocínio.
    /// </summary>
    /// <param name="modelOutput">Texto produzido pelo modelo.</param>
    /// <returns>Artefato útil ou string vazia quando não há conteúdo aproveitável.</returns>
    public static string ExtractStructuredPayload(string modelOutput)
    {
        if (string.IsNullOrWhiteSpace(modelOutput))
        {
            return string.Empty;
        }

        var fromFence = TryExtractFencedPayload(modelOutput);

        if (!string.IsNullOrEmpty(fromFence))
        {
            return fromFence;
        }

        var fromJson = TryExtractBalancedJson(modelOutput);

        return !string.IsNullOrEmpty(fromJson) ? fromJson : StripReasoning(modelOutput);
    }

    /// <summary>Concatena os fragmentos do candidato, descartando os marcados como raciocínio.</summary>
    private static string ReadCandidateText(Candidate candidate)
    {
        var parts = candidate.Content?.Parts;

        if (parts is null || parts.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();

        foreach (var part in parts)
        {
            if (part.Thought is true || string.IsNullOrEmpty(part.Text))
            {
                continue;
            }

            builder.Append(part.Text);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Devolve o conteúdo do bloco cercado mais adequado: prioriza o rotulado como JSON ou cujo
    /// conteúdo já é JSON válido; na ausência, o primeiro bloco não vazio.
    /// </summary>
    private static string? TryExtractFencedPayload(string text)
    {
        string? firstBlock = null;

        foreach (Match match in FencedBlockRegex.Matches(text))
        {
            var content = match.Groups["content"].Value.Trim();

            if (content.Length == 0)
            {
                continue;
            }

            var label = match.Groups["info"].Value.Trim();

            if (label.Contains("json", StringComparison.OrdinalIgnoreCase) || IsJsonDocument(content))
            {
                return content;
            }

            firstBlock ??= content;
        }

        return firstBlock;
    }

    /// <summary>
    /// Procura o primeiro trecho JSON balanceado e válido do texto (ignorando delimitadores dentro
    /// de strings), usado quando o modelo não cerca a resposta com crases triplas.
    /// </summary>
    private static string? TryExtractBalancedJson(string text)
    {
        // Limite de tentativas evita varredura quadrática em saídas longas de raciocínio.
        const int MaxAttempts = 32;

        var attempts = 0;

        for (var start = 0; start < text.Length && attempts < MaxAttempts; start++)
        {
            var opener = text[start];

            if (opener != '{' && opener != '[')
            {
                continue;
            }

            attempts++;

            var end = FindBalancedEnd(text, start);

            if (end < 0)
            {
                continue;
            }

            var candidate = text[start..end];

            if (HasStructuredContent(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Indica se o candidato é JSON válido com conteúdo estruturado (objeto com membros ou array
    /// com itens). Blocos vazios ("{ }", "[ ]") aparecem naturalmente em código-fonte C#/Java e
    /// por isso não são tratados como payload.
    /// </summary>
    private static bool HasStructuredContent(string candidate)
    {
        try
        {
            using var document = JsonDocument.Parse(candidate.Trim());

            return document.RootElement.ValueKind switch
            {
                JsonValueKind.Object => document.RootElement.EnumerateObject().Any(),
                JsonValueKind.Array => document.RootElement.GetArrayLength() > 0,
                _ => false
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Devolve o índice (exclusivo) do fechamento que equilibra a abertura em
    /// <paramref name="start"/>, ou <c>-1</c> quando o trecho não fecha corretamente.
    /// </summary>
    private static int FindBalancedEnd(string text, int start)
    {
        var expectedCloser = text[start] == '{' ? '}' : ']';
        var depth = 0;
        var insideString = false;
        var escaped = false;

        for (var index = start; index < text.Length; index++)
        {
            var current = text[index];

            if (insideString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (current == '\\')
                {
                    escaped = true;
                }
                else if (current == '"')
                {
                    insideString = false;
                }

                continue;
            }

            switch (current)
            {
                case '"':
                    insideString = true;
                    break;
                case '{' or '[':
                    depth++;
                    break;
                case '}' or ']':
                    depth--;

                    if (depth == 0)
                    {
                        return current == expectedCloser ? index + 1 : -1;
                    }

                    break;
            }
        }

        return -1;
    }

    /// <summary>
    /// Valida se o trecho é um documento JSON bem formado (objeto ou array). Usado também pelo
    /// adapter para distinguir erro de protocolo (JSON sem <c>candidates</c>) de texto puro do modelo.
    /// </summary>
    public static bool IsJsonDocument(string candidate)
    {
        var trimmed = candidate.Trim();

        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Sanitiza o payload devolvido pelo LLM antes da desserialização: modelos entregam o JSON cercado
    /// por crases triplas — por vezes rotuladas com <c>json</c> — ou com o rótulo colado no objeto, e o
    /// desserializador só aceita a string bruta do objeto.
    /// </summary>
    /// <remarks>
    /// A limpeza agressiva (remoção de cercas e do rótulo) só roda quando o texto <b>não</b> é JSON
    /// válido: assim um valor de string que contenha crases legítimas (um trecho de markdown dentro do
    /// arquivo gerado) nunca é mutilado. Quando nem isso resolve, devolve-se o primeiro trecho JSON
    /// balanceado — o caso da cerca rotulada que veio com prosa dentro dela.
    /// </remarks>
    /// <param name="modelOutput">Saída do modelo (bruta ou já sem o raciocínio).</param>
    /// <returns>Payload pronto para desserializar; string vazia quando não há texto.</returns>
    public static string SanitizeJsonPayload(string modelOutput)
    {
        if (string.IsNullOrWhiteSpace(modelOutput))
        {
            return string.Empty;
        }

        var text = modelOutput.Trim();

        if (IsJsonDocument(text))
        {
            // Já é JSON: nada a limpar — e nenhuma crase dentro de string é tocada.
            return text;
        }

        var fenced = TryExtractFencedPayload(text);

        if (!string.IsNullOrEmpty(fenced) && IsJsonDocument(fenced))
        {
            return fenced;
        }

        var stripped = StripFenceResidue(text);

        if (IsJsonDocument(stripped))
        {
            return stripped;
        }

        return TryExtractBalancedJson(stripped) ?? stripped;
    }

    /// <summary>
    /// Remove os resíduos de markdown do candidato: cercas em linha própria (com o rótulo de
    /// linguagem), cercas coladas no payload e o rótulo <c>json</c> solto antes do objeto.
    /// </summary>
    private static string StripFenceResidue(string text)
    {
        var stripped = FenceLineRegex.Replace(text, string.Empty);

        stripped = InlineFenceRegex.Replace(stripped, string.Empty);
        stripped = LeadingJsonLabelRegex.Replace(stripped, string.Empty);

        return stripped.Trim();
    }

    /// <summary>
    /// Remove as marcas de raciocínio (tags de thinking, tokens de controle do template e linhas
    /// "* Input:/Constraint:") preservando o restante do texto como artefato.
    /// </summary>
    private static string StripReasoning(string modelOutput)
    {
        var text = ControlTokenRegex.Replace(modelOutput, string.Empty);

        text = ReasoningBlockRegex.Replace(text, string.Empty);
        text = ReasoningLineRegex.Replace(text, string.Empty);

        return text.Trim();
    }
}

