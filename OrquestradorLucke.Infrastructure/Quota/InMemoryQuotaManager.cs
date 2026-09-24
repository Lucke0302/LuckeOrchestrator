using System.Collections.Concurrent;
using OrquestradorLucke.Application.Interfaces;

namespace OrquestradorLucke.Infrastructure.Quota;

/// <summary>
/// Circuit Breaker de cota mantido em memória do processo (registrado como Singleton).
/// Guarda, por modelo, o instante UTC em que ele volta a estar disponível — janela de 24 horas
/// no caso de rate limit (HTTP 429). Sem persistência: reiniciar o host libera os bloqueios.
/// </summary>
public sealed class InMemoryQuotaManager : IQuotaManager
{
    private readonly ConcurrentDictionary<string, DateTime> _blockedUntilUtc =
        new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public bool IsModelAvailable(string modelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        if (!_blockedUntilUtc.TryGetValue(modelName, out var blockedUntil))
        {
            return true;
        }

        if (blockedUntil > DateTime.UtcNow)
        {
            return false;
        }

        // Janela expirada: libera o modelo (purge oportunista, sem timer dedicado).
        _blockedUntilUtc.TryRemove(new KeyValuePair<string, DateTime>(modelName, blockedUntil));

        return true;
    }

    /// <inheritdoc />
    public void LockOutModel(string modelName, TimeSpan lockoutDuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        if (lockoutDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lockoutDuration),
                lockoutDuration,
                "A janela de bloqueio de cota deve ser maior que zero.");
        }

        var newDeadlineUtc = DateTime.UtcNow.Add(lockoutDuration);

        // Preserva a janela mais longa quando já existe bloqueio ativo para o mesmo modelo.
        _blockedUntilUtc.AddOrUpdate(
            modelName,
            newDeadlineUtc,
            (_, currentDeadlineUtc) => newDeadlineUtc > currentDeadlineUtc ? newDeadlineUtc : currentDeadlineUtc);
    }
}
