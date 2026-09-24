using OrquestradorLucke.Domain;

namespace OrquestradorLucke.Application.Interfaces;

/// <summary>
/// Roteador dinâmico (MoE) que decide qual execução atende cada tarefa,
/// com base na complexidade e no estado de frustração da execução.
/// </summary>
public interface ITaskRouter
{
    /// <summary>Resolve o expert adequado à complexidade da tarefa.</summary>
    /// <param name="complexity">Complexidade classificada da tarefa.</param>
    /// <returns>O provedor de LLM que executará a tarefa.</returns>
    ILLMProvider ResolveProvider(TaskComplexity complexity);

    /// <summary>
    /// Resolve o expert mais robusto quando o medidor de frustração atinge o limite
    /// e o circuito desarma (overdrive).
    /// </summary>
    ILLMProvider ResolveOverdriveProvider();
}
