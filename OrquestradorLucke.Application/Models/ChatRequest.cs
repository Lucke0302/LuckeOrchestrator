namespace OrquestradorLucke.Application.Models;

/// <summary>
/// Corpo de <c>POST /api/chat</c>: a mensagem do operador que o motor de chat envia ao modelo.
/// </summary>
/// <param name="Message">Mensagem em texto do operador (o raciocínio volta embrulhado em tags de pensamento).</param>
public sealed record ChatRequest(string Message);
