namespace OrquestradorLucke.Application.Interfaces;

/// <summary>
/// Abstração de um expert de LLM no padrão MoE. As implementações concretas
/// (adaptadores HTTP) pertencem exclusivamente à camada Infrastructure.
/// </summary>
public interface ILLMProvider
{
    /// <summary>Identificação do modelo atendido por este provedor, para logs e telemetria.</summary>
    string ModelName { get; }

    /// <summary>Analisa o contexto/payload da tarefa e retorna o entendimento produzido.</summary>
    Task<string> AnalyzeContextAsync(string payload, CancellationToken cancellationToken);

    /// <summary>
    /// Gera os artefatos (um ou mais arquivos) que atendem à tarefa, a partir do payload e da
    /// análise de contexto.
    /// </summary>
    /// <param name="payload">Conteúdo bruto da tarefa.</param>
    /// <param name="contextAnalysis">Contexto recuperado do RAG (pode vir vazio).</param>
    /// <param name="cancellationToken">Token de cancelamento da iteração do worker.</param>
    /// <returns>
    /// Alterações a publicar: a chave é o caminho relativo do arquivo no repositório e o valor é o
    /// conteúdo completo do arquivo. Um dicionário vazio indica ausência de payload aproveitável —
    /// o chamador contabiliza a falha na mecânica de frustração.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Quando a resposta do provedor não é o objeto JSON estrito esperado
    /// (<c>{"caminho/do/arquivo.cs": "conteúdo do código"}</c>). O parse inválido é convertido em
    /// falha de propósito: a mecânica de frustração a contabiliza e o roteador escala para o próximo
    /// expert da cadeia MoE (overdrive ao atingir o limite).
    /// </exception>
    Task<Dictionary<string, string>> GenerateCodeAsync(string payload, string contextAnalysis, CancellationToken cancellationToken);

    /// <summary>
    /// Avalia a falha da execução e retorna o diagnóstico a ser usado como
    /// contexto na próxima tentativa (retorno vazio indica ausência de diagnóstico).
    /// </summary>
    Task<string> EvaluateErrorAsync(string payload, string generatedCode, string errorMessage, CancellationToken cancellationToken);
}
