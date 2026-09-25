namespace OrquestradorLucke.Application.Models;

/// <summary>
/// Par de tokens devolvido por <c>POST /api/auth/login</c> e <c>POST /api/auth/refresh</c>: o access
/// token (curto, no cabeçalho <c>Authorization</c>) e o refresh token rotativo (uma semana) com os
/// respectivos instantes de expiração — é o cliente que decide quando renovar.
/// </summary>
/// <param name="AccessToken">JWT assinado, válido por <c>Jwt:AccessTokenMinutes</c> (15 minutos).</param>
/// <param name="RefreshToken">String aleatória de 256 bits que substitui o valor anterior a cada uso.</param>
/// <param name="AccessTokenExpiresAtUtc">Instante UTC de expiração do access token.</param>
/// <param name="RefreshTokenExpiresAtUtc">Instante UTC de expiração do refresh token (7 dias).</param>
public sealed record AuthTokenResponse(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessTokenExpiresAtUtc,
    DateTimeOffset RefreshTokenExpiresAtUtc);
