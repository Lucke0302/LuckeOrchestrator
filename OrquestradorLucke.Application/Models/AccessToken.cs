namespace OrquestradorLucke.Application.Models;

/// <summary>Access token JWT emitido para uma conta e o instante (UTC) em que ele deixa de valer.</summary>
/// <param name="Token">Token compacto (JWS), enviado como <c>Authorization: Bearer &lt;token&gt;</c>.</param>
/// <param name="ExpiresAtUtc">Instante UTC de expiração (15 minutos após a emissão, por padrão).</param>
public sealed record AccessToken(string Token, DateTimeOffset ExpiresAtUtc);
