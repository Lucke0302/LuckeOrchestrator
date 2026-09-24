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

    /// <summary>Gera o código/artefato a partir do payload e da análise de contexto.</summary>
    Task<string> GenerateCodeAsync(string payload, string contextAnalysis, CancellationToken cancellationToken);

    /// <summary>
    /// Avalia a falha da execução e retorna o diagnóstico a ser usado como
    /// contexto na próxima tentativa (retorno vazio indica ausência de diagnóstico).
    /// </summary>
    Task<string> EvaluateErrorAsync(string payload, string generatedCode, string errorMessage, CancellationToken cancellationToken);
}
