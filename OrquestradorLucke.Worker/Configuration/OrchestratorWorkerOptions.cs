namespace OrquestradorLucke.Worker.Configuration;

/// <summary>
/// Opções do host do orquestrador (cadência do laço do BackgroundService).
/// </summary>
public sealed class OrchestratorWorkerOptions
{
    public const string SectionName = "Orchestrator";

    /// <summary>Intervalo entre ciclos do laço de orquestração, em segundos.</summary>
    public int PollingIntervalSeconds { get; set; }

    /// <summary>
    /// Cooldown aplicado ao laço quando toda a cadeia MoE está bloqueada pelo Circuit Breaker de
    /// cota (HTTP 429), em minutos. Enquanto o worker dorme, a tarefa permanece na fila e o rate
    /// limit do provedor deixa de ser pressionado.
    /// </summary>
    public int QuotaCooldownMinutes { get; set; } = 15;

    /// <summary>
    /// Caminho relativo (dentro do repositório de trabalho) do arquivo que recebe o artefato gerado
    /// pelo expert. É o conteúdo enviado em <c>CommitChangesAsync</c>.
    /// </summary>
    public string GeneratedArtifactPath { get; set; } = "Feature.cs";
}
