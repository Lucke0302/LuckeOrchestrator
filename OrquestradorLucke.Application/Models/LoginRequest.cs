namespace OrquestradorLucke.Application.Models;

/// <summary>
/// Corpo de <c>POST /api/auth/login</c>: as credenciais do operador que troca por um par de tokens.
/// </summary>
/// <param name="Username">Nome de login (comparado na forma canônica, sem diferenciar maiúsculas).</param>
/// <param name="Password">Senha em claro do usuário — é convertida em hash SHA256 e descartada no mesmo request.</param>
public sealed record LoginRequest(string Username, string Password);
