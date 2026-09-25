namespace OrquestradorLucke.Domain;

/// <summary>
/// Conta autenticável da plataforma web: identifica quem chama a API de gerenciamento/revisão e o hub
/// de streaming de logs. A senha nunca é guardada em claro (só o hash SHA256) e o refresh token
/// rotativo é o que permite renovar o access token sem novo login.
/// </summary>
public record User
{
    /// <summary>Identificador único do usuário.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Nome de login na forma canônica (<see cref="NormalizeUsername"/>) — chave natural da conta e
    /// única no banco.
    /// </summary>
    public required string Username { get; init; }

    /// <summary>Hash SHA256 (hexadecimal) da senha. A senha em claro não existe no domínio nem no banco.</summary>
    public required string PasswordHash { get; init; }

    /// <summary>
    /// Refresh token atual (string aleatória de 256 bits). Nulo enquanto a conta nunca autenticou; cada
    /// refresh substitui o valor anterior, então o token antigo deixa de valer.
    /// </summary>
    public string? RefreshToken { get; init; }

    /// <summary>Instante (UTC) em que o <see cref="RefreshToken"/> deixa de valer (7 dias após a emissão).</summary>
    public DateTimeOffset? RefreshTokenExpiry { get; init; }

    /// <summary>Momento de criação da conta (UTC).</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Forma canônica do nome de login: sem espaços nas pontas e em minúsculas. É este valor que a
    /// coluna <c>username</c> grava e que o login compara — o índice único da tabela, portanto, também
    /// não diferencia maiúsculas de minúsculas.
    /// </summary>
    /// <param name="username">Nome de login recebido do cliente ou da configuração de bootstrap.</param>
    /// <returns>O nome de login pronto para gravar/comparar.</returns>
    /// <exception cref="ArgumentException">Quando <paramref name="username"/> for nulo, vazio ou apenas espaços.</exception>
    public static string NormalizeUsername(string username)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        return username.Trim().ToLowerInvariant();
    }
}
