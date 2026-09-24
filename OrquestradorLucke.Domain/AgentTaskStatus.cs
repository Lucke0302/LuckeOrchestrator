namespace OrquestradorLucke.Domain;

/// <summary>
/// Estágio do ciclo de vida de uma <see cref="AgentTask"/> dentro do orquestrador.
/// </summary>
public enum AgentTaskStatus
{
    /// <summary>Aguardando ser roteada/executada.</summary>
    Pendente = 0,

    /// <summary>Em execução pelo expert selecionado.</summary>
    EmExecucao = 1,

    /// <summary>Executada com sucesso.</summary>
    Concluida = 2,

    /// <summary>Interrompida por falha não recuperável.</summary>
    Falhou = 3,

    /// <summary>Cancelada por solicitação ou desligamento do host.</summary>
    Cancelada = 4
}
