namespace OrquestradorLucke.Domain;

/// <summary>
/// Nível de complexidade da tarefa. Utilizado pelo roteador dinâmico (MoE)
/// para selecionar o modelo mais adequado ao esforço exigido.
/// </summary>
public enum TaskComplexity
{
    /// <summary>Tarefas triviais, sem contexto relevante.</summary>
    Baixo = 0,

    /// <summary>Tarefas de esforço intermediário.</summary>
    Medio = 1,

    /// <summary>Tarefas que exigem maior capacidade de raciocínio.</summary>
    Alto = 2,

    /// <summary>Tarefas críticas, candidatas ao modelo mais robusto.</summary>
    Critico = 3
}
