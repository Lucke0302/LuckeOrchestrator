namespace OrquestradorLucke.Application.Models;

/// <summary>
/// Corpo de <c>POST /api/tasks/{id}/reject</c>: o motivo pelo qual o revisor humano devolve a entrega.
/// </summary>
/// <param name="Motivo">
/// Justificativa da rejeição. Vai como comentário do pull request fechado e entra no histórico de
/// frustração do daemon (é o texto que o overdrive recebe para não repetir o erro).
/// </param>
public sealed record RejectAgentTaskRequest(string Motivo);
