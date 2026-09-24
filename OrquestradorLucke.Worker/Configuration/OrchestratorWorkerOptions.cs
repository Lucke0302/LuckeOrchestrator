namespace OrquestradorLucke.Worker.Configuration;

/// <summary>
/// Opções do host do orquestrador (cadência do laço do BackgroundService).
/// </summary>
public sealed class OrchestratorWorkerOptions
{
    public const string SectionName = "Orchestrator";

    /// <summary>Intervalo entre ciclos do laço de orquestração, em segundos.</summary>
    public int PollingIntervalSeconds { get; set; }
}
