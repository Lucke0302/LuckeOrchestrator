namespace OrquestradorLucke.Application.Models;

/// <summary>Desfecho de um caso de uso de autenticação (login ou refresh).</summary>
public enum AuthenticationOutcome
{
    /// <summary>Par de tokens emitido: credenciais validadas ou refresh token rotacionado.</summary>
    Sucesso = 0,

    /// <summary>Usuário inexistente ou senha incorreta — a resposta não distingue os dois casos.</summary>
    CredenciaisInvalidas = 1,

    /// <summary>Refresh token desconhecido, já rotacionado ou expirado: é preciso autenticar de novo.</summary>
    RefreshTokenInvalido = 2
}

/// <summary>
/// Resultado de um caso de uso de autenticação: o desfecho, o par de tokens (quando houver) e o
/// detalhe que explica a recusa. O host traduz isso em status HTTP — a Application não conhece HTTP.
/// </summary>
/// <param name="Outcome">Desfecho da operação.</param>
/// <param name="Tokens">Par de tokens emitido, nulo quando a autenticação foi recusada.</param>
/// <param name="Detail">Explicação curta da recusa, publicada no corpo do erro da API.</param>
public sealed record AuthenticationResult(AuthenticationOutcome Outcome, AuthTokenResponse? Tokens, string? Detail)
{
    /// <summary>Indica que houve emissão de tokens.</summary>
    public bool Succeeded => Outcome is AuthenticationOutcome.Sucesso;

    /// <summary>Autenticação concluída: par de tokens emitido.</summary>
    public static AuthenticationResult Sucesso(AuthTokenResponse tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        return new AuthenticationResult(AuthenticationOutcome.Sucesso, tokens, null);
    }

    /// <summary>Login recusado (usuário inexistente ou senha incorreta, sem revelar qual dos dois).</summary>
    public static AuthenticationResult CredenciaisInvalidas()
        => new(AuthenticationOutcome.CredenciaisInvalidas, null, "Usuário ou senha inválidos.");

    /// <summary>Refresh recusado: token desconhecido, já rotacionado ou expirado.</summary>
    public static AuthenticationResult RefreshTokenInvalido()
        => new(AuthenticationOutcome.RefreshTokenInvalido, null, "Refresh token inválido ou expirado: faça login novamente.");
}
