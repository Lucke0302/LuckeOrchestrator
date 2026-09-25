using System.Security.Cryptography;
using System.Text;

namespace OrquestradorLucke.Application.Services;

/// <summary>
/// Hash de senha (SHA256 simples) e sua verificação. Vive na Application porque é regra de negócio da
/// autenticação: o Domain não conhece criptografia e a Infrastructure não guarda nem compara senha.
/// </summary>
/// <remarks>
/// A senha em claro nunca é persistida nem comparada: o banco guarda o hash hexadecimal e o login
/// recalcula o hash da senha recebida. A comparação usa <see cref="CryptographicOperations.FixedTimeEquals"/>
/// (tempo constante) — comparar as strings com <c>==</c> vazaria o hash por diferença de tempo de
/// resposta, igual à validação do HMAC do webhook.
/// </remarks>
public static class PasswordHasher
{
    /// <summary>Tamanho, em caracteres, do hash hexadecimal devolvido por <see cref="ComputeHash"/> (32 bytes do SHA256).</summary>
    public const int HashLength = 64;

    /// <summary>Calcula o hash SHA256 (hexadecimal, minúsculo) da senha informada.</summary>
    /// <param name="password">Senha em claro recebida no login ou no seed do usuário inicial.</param>
    /// <returns>O hash hexadecimal com <see cref="HashLength"/> caracteres.</returns>
    /// <exception cref="ArgumentException">Quando <paramref name="password"/> for nula, vazia ou apenas espaços.</exception>
    public static string ComputeHash(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(password)));
    }

    /// <summary>
    /// Compara a senha recebida com o hash gravado, sem revelar por tempo de resposta se a conta existe
    /// ou quantos caracteres do hash estão certos.
    /// </summary>
    /// <param name="password">Senha em claro recebida no login.</param>
    /// <param name="passwordHash">Hash hexadecimal gravado na conta.</param>
    /// <returns><c>true</c> apenas quando o hash da senha confere com o gravado.</returns>
    public static bool Verify(string password, string passwordHash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(passwordHash))
        {
            return false;
        }

        byte[] expectedHash;

        try
        {
            expectedHash = Convert.FromHexString(passwordHash);
        }
        catch (FormatException)
        {
            // Hash corrompido (não hexadecimal): nenhuma senha casa com ele.
            return false;
        }

        var computedHash = SHA256.HashData(Encoding.UTF8.GetBytes(password));

        return CryptographicOperations.FixedTimeEquals(computedHash, expectedHash);
    }
}
