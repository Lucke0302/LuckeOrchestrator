namespace OrquestradorLucke.Application.Configuration;

/// <summary>
/// Parâmetros da mecânica de frustração (regra de negócio): quantas falhas são toleradas
/// antes de o circuito desarmar e a tarefa migrar para o modelo mais robusto.
/// </summary>
public sealed class FrustrationSettings
{
    public const string SectionName = "Frustration";

    /// <summary>Limite de falhas antes do overdrive. Sem valor default: deve vir da configuração.</summary>
    public int MaxFailures { get; set; }
}
