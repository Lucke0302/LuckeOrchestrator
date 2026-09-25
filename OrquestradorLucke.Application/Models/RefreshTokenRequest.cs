namespace OrquestradorLucke.Application.Models;

/// <summary>
/// Corpo de <c>POST /api/auth/refresh</c>: o refresh token que o cliente guardou após o login, trocado
/// por um par novo sem exigir a senha de novo.
/// </summary>
/// <param name="RefreshToken">Refresh token vigente; o valor é rotacionado a cada refresh.</param>
public sealed record RefreshTokenRequest(string RefreshToken);
