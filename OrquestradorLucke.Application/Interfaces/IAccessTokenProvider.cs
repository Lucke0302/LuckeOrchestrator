using OrquestradorLucke.Application.Models;
using OrquestradorLucke.Domain;

namespace OrquestradorLucke.Application.Interfaces;

/// <summary>
/// Emissão do access token JWT. A assinatura (HS256 com o segredo de <c>Jwt:Secret</c>) é detalhe de
/// implementação e vive na Infrastructure; o caso de uso de autenticação conhece apenas o contrato.
/// </summary>
public interface IAccessTokenProvider
{
    /// <summary>
    /// Assina um access token para a conta informada, com <c>sub</c>/<c>unique_name</c> do usuário e a
    /// validade de <c>Jwt:AccessTokenMinutes</c>.
    /// </summary>
    /// <param name="user">Conta autenticada para a qual o token é emitido.</param>
    /// <returns>O token compacto (JWS) e o instante UTC em que ele expira.</returns>
    AccessToken CreateAccessToken(User user);
}
