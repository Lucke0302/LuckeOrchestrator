using OrquestradorLucke.Application.Configuration;
using OrquestradorLucke.Application.Interfaces;
using OrquestradorLucke.Domain;

namespace OrquestradorLucke.Application.Services;

/// <summary>
/// Seed do usuário inicial: sem uma conta na tabela <c>users</c> não há como autenticar (não existe
/// rota de registro — o cadastro é do operador, e a primeira conta entra aqui). Roda no start do
/// daemon, antes de a API começar a atender.
/// </summary>
/// <remarks>
/// Só escreve quando as duas condições valem: as credenciais estão configuradas (<c>Auth:Username</c>
/// e <c>Auth:Password</c>, via user-secrets/variável de ambiente) e a tabela está vazia. Sem isso, a
/// senha em claro nunca chega ao banco: o que é gravado é o hash SHA256 do login.
/// </remarks>
public sealed class AuthBootstrapService(IUserRepository userRepository, AuthBootstrapSettings settings)
{
    /// <summary>
    /// Cria a conta inicial quando ela ainda não existe e as credenciais estão configuradas.
    /// </summary>
    /// <param name="cancellationToken">Token de cancelamento do start do host.</param>
    /// <returns>
    /// <c>true</c> quando a conta foi criada nesta execução; <c>false</c> quando não havia nada a
    /// configurar ou quando a tabela já tinha contas (o seed é idempotente e não sobrescreve senha).
    /// </returns>
    public async Task<bool> EnsureBootstrapUserAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Username) || string.IsNullOrWhiteSpace(settings.Password))
        {
            return false;
        }

        if (await userRepository.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            // Já existe conta: a senha em vigor é a do banco, não a da configuração — sobrescrever aqui
            // trocaria a senha do operador a cada restart.
            return false;
        }

        var user = new User
        {
            Username = User.NormalizeUsername(settings.Username),
            PasswordHash = PasswordHasher.ComputeHash(settings.Password)
        };

        await userRepository.AddAsync(user, cancellationToken).ConfigureAwait(false);

        return true;
    }
}
