namespace OrquestradorLucke.Application.Configuration;

/// <summary>
/// Credenciais do usuário inicial, usadas apenas para semear a tabela <c>users</c> no primeiro start
/// do daemon (sem conta não há como autenticar). A senha vem da configuração — user-secrets ou
/// variável de ambiente <c>Auth__Password</c> — e é gravada só como hash SHA256.
/// </summary>
/// <remarks>
/// Este não é um cadastro público: não existe rota de registro. A conta inicial entra por aqui e as
/// demais são criadas pelo operador no banco (mesmo valor de hash do login). A seção vazia simplesmente
/// não semeia nada.
/// </remarks>
public sealed class AuthBootstrapSettings
{
    public const string SectionName = "Auth";

    /// <summary>Nome de login do usuário inicial (normalizado por <c>User.NormalizeUsername</c> ao gravar).</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Senha em claro do usuário inicial — lida da configuração apenas para gerar o hash do seed.</summary>
    public string Password { get; set; } = string.Empty;
}
