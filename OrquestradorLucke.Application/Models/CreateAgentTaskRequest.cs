using OrquestradorLucke.Domain;

namespace OrquestradorLucke.Application.Models;

/// <summary>
/// Corpo de <c>POST /api/tasks</c>: o pedido do usuário e a complexidade classificada que alimenta o
/// roteador MoE.
/// </summary>
/// <param name="Payload">Conteúdo bruto da tarefa (issue, comentário ou instrução do usuário).</param>
/// <param name="Complexidade">
/// Complexidade que o roteador MoE usa para escolher o expert. Opcional: quando o JSON não traz o
/// campo, vale <see cref="TaskComplexity.Baixo"/> (a cadeia de modelos mais barata).
/// </param>
public sealed record CreateAgentTaskRequest(
    string Payload,
    TaskComplexity Complexidade = TaskComplexity.Baixo);
