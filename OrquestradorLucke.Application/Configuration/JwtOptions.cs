namespace OrquestradorLucke.Application.Configuration;

/// <summary>
/// Parâmetros de emissão e validação do JWT: assinatura simétrica (HS256), emissor/público e as duas
/// validades do par de tokens. O segredo (<c>Jwt:Secret</c>) nunca é hardcoded — vem de user-secrets
/// ou da variável de ambiente <c>Jwt__Secret</c>, como todos os demais segredos do daemon.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>
    /// Tamanho mínimo do segredo, em caracteres: o HS256 assina com chave de 256 bits e um segredo
    /// menor seria recusado pelo provedor em tempo de execução — melhor falhar no start do daemon.
    /// </summary>
    public const int MinimumSecretLength = 32;

    /// <summary>Segredo compartilhado da assinatura simétrica (HS256).</summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>Emissor gravado em <c>iss</c> e exigido na validação.</summary>
    public string Issuer { get; set; } = "OrquestradorLucke";

    /// <summary>Público gravado em <c>aud</c> e exigido na validação.</summary>
    public string Audience { get; set; } = "OrquestradorLucke.Panel";

    /// <summary>Validade do access token, em minutos (15 por padrão).</summary>
    public int AccessTokenMinutes { get; set; } = 15;

    /// <summary>Validade do refresh token, em dias (uma semana por padrão).</summary>
    public int RefreshTokenDays { get; set; } = 7;

    /// <summary>Indica se o segredo configurado tem tamanho suficiente para o HMAC-SHA256.</summary>
    public bool HasUsableSecret => !string.IsNullOrWhiteSpace(Secret) && Secret.Length >= MinimumSecretLength;

    /// <summary>
    /// Falha rápido quando a configuração não serve para assinar/validar tokens: sem segredo (ou com um
    /// curto demais) o host subiria com a autenticação quebrada e só descobriria no primeiro login.
    /// </summary>
    /// <exception cref="InvalidOperationException">Quando o segredo não está definido, é curto demais ou o emissor/público está em branco.</exception>
    public void EnsureUsable()
    {
        if (!HasUsableSecret)
        {
            throw new InvalidOperationException(
                $"A chave de configuração '{SectionName}:Secret' (variável de ambiente {SectionName}__Secret) não está " +
                $"definida ou tem menos de {MinimumSecretLength} caracteres — o HMAC-SHA256 exige uma chave de 256 bits. " +
                "Defina o segredo via user-secrets ou variável de ambiente.");
        }

        if (string.IsNullOrWhiteSpace(Issuer) || string.IsNullOrWhiteSpace(Audience))
        {
            // Emissor/público em branco quebrariam a validação do token no host: falhar aqui aponta a
            // causa antes de a API começar a responder 401 sem explicação.
            throw new InvalidOperationException(
                $"As chaves '{SectionName}:Issuer' e '{SectionName}:Audience' precisam estar preenchidas para " +
                "assinar e validar o access token.");
        }
    }

    /// <summary>
    /// Validade do access token como <see cref="TimeSpan"/>, com o piso de 1 minuto aplicado — uma
    /// configuração zerada não pode emitir token já expirado.
    /// </summary>
    public TimeSpan AccessTokenLifetime => TimeSpan.FromMinutes(Math.Max(1, AccessTokenMinutes));

    /// <summary>
    /// Validade do refresh token como <see cref="TimeSpan"/>, com o piso de 1 dia aplicado (a regra de
    /// negócio é uma semana; o piso só protege contra configuração inválida).
    /// </summary>
    public TimeSpan RefreshTokenLifetime => TimeSpan.FromDays(Math.Max(1, RefreshTokenDays));
}
