namespace OrquestradorLucke.Application.Models;

/// <summary>
/// Resposta de <c>POST /api/chat</c>: o texto do modelo já com o raciocínio isolado pelo contrato de
/// tags e a nota de persistência (modelo e tentativas) anexada no fim.
/// </summary>
/// <param name="Response">
/// Texto final do modelo — pronto para o painel separar o bloco de raciocínio da resposta exibida.
/// </param>
public sealed record ChatResponse(string Response);
