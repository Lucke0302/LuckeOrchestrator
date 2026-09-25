using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OrquestradorLucke.Application.Models;
using OrquestradorLucke.Application.Services;

namespace OrquestradorLucke.Worker.Endpoints;

/// <summary>
/// Minimal API de autenticação: troca credenciais por um par de tokens e renova o par sem exigir a
/// senha de novo. É o único grupo <b>anônimo</b> do host — o restante da API e o hub de logs exigem o
/// access token.
/// </summary>
/// <remarks>
/// <para>Rotas (todas sob <c>/api/auth</c>):</para>
/// <list type="bullet">
/// <item><description><c>POST /api/auth/login</c> — valida usuário/senha (hash SHA256) e devolve access token (15 min) + refresh token (7 dias).</description></item>
/// <item><description><c>POST /api/auth/refresh</c> — troca um refresh token vigente por um par novo, rotacionando o refresh.</description></item>
/// </list>
/// <para>
/// A recusa é sempre <c>401</c> com o mesmo detalhe para usuário inexistente e senha errada: a resposta
/// não confirma se o login existe (o caso de uso compara o hash em tempo constante). O log técnico sai
/// pelo pipeline de <c>ILogger</c>, não na resposta HTTP.
/// </para>
/// </remarks>
public static class AuthEndpoints
{
    /// <summary>Prefixo das rotas de autenticação.</summary>
    public const string RoutePrefix = "/api/auth";

    /// <summary>Registra as rotas de autenticação no host.</summary>
    /// <param name="endpoints">Construtor de rotas do host.</param>
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // AllowAnonymous explícito: o login não pode exigir o token que ele mesmo emite.
        var group = endpoints.MapGroup(RoutePrefix).AllowAnonymous();

        group.MapPost("/login", LoginAsync);
        group.MapPost("/refresh", RefreshAsync);

        return endpoints;
    }

    /// <summary>Troca credenciais por um par de tokens (access de 15 minutos, refresh de 7 dias).</summary>
    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        AuthenticationService authenticationService,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return Results.Problem(
                title: "Credenciais obrigatórias",
                detail: "Os campos 'username' e 'password' são obrigatórios.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await authenticationService
            .LoginAsync(request.Username, request.Password, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            // O nome de login tentado fica no log (útil para perceber tentativa de força bruta), mas
            // nunca vai para a resposta — lá o detalhe não distingue usuário inexistente de senha errada.
            loggerFactory
                .CreateLogger("OrquestradorLucke.Api.Auth")
                .LogWarning("Login recusado para o usuário '{Username}'.", request.Username);

            return Results.Problem(
                title: "Credenciais inválidas",
                detail: result.Detail,
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return Results.Ok(result.Tokens);
    }

    /// <summary>Renova o par de tokens a partir do refresh token vigente (rotação a cada uso).</summary>
    private static async Task<IResult> RefreshAsync(
        RefreshTokenRequest request,
        AuthenticationService authenticationService,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return Results.Problem(
                title: "Refresh token obrigatório",
                detail: "O campo 'refreshToken' é obrigatório.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await authenticationService
            .RefreshAsync(request.RefreshToken, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            // Token desconhecido, já rotacionado (uso repetido) ou expirado: o painel precisa voltar à
            // tela de login, e o 401 é o sinal para isso.
            return Results.Problem(
                title: "Refresh token inválido",
                detail: result.Detail,
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return Results.Ok(result.Tokens);
    }
}
