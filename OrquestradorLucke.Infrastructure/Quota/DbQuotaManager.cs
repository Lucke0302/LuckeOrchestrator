using Microsoft.EntityFrameworkCore;
using OrquestradorLucke.Application.Interfaces;
using OrquestradorLucke.Domain;
using OrquestradorLucke.Infrastructure.Data;

namespace OrquestradorLucke.Infrastructure.Quota;

/// <summary>
/// Circuit Breaker de cota persistido no PostgreSQL (tabela <c>quota_states</c>), registrado como
/// Scoped junto do <see cref="AppDbContext"/>. Ao receber HTTP 429 o modelo é retirado do rodízio
/// MoE até a janela de bloqueio expirar — estado que sobrevive ao reinício do daemon e é lido por
/// todas as instâncias que compartilham o banco.
/// </summary>
/// <remarks>
/// O contrato <see cref="IQuotaManager"/> é síncrono (consumido pelo roteador MoE e pelos
/// adapters), por isso as operações usam as APIs <b>síncronas</b> do EF Core — e não
/// <c>sync-over-async</c>, que prenderia uma thread do pool aguardando I/O.
/// </remarks>
public sealed class DbQuotaManager(AppDbContext context) : IQuotaManager
{
    /// <inheritdoc />
    public bool IsModelAvailable(string modelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        // Leitura sem rastreamento: a consulta apenas decide o resultado; nenhuma instância precisa
        // ficar anexada ao contexto do escopo (o bloqueio é gravado por LockOutModel).
        var state = context.QuotaStates
            .AsNoTracking()
            .FirstOrDefault(candidate => candidate.ProviderName == modelName);

        if (state is null)
        {
            return true;
        }

        if (state.LockedUntil > DateTimeOffset.UtcNow)
        {
            return false;
        }

        // Janela expirada: remove o bloqueio (purge oportunista, sem timer dedicado) e libera o modelo.
        context.QuotaStates
            .Where(candidate => candidate.ProviderName == modelName)
            .ExecuteDelete();

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

        var lockedUntil = DateTimeOffset.UtcNow.Add(lockoutDuration);

        var current = context.QuotaStates
            .AsNoTracking()
            .FirstOrDefault(candidate => candidate.ProviderName == modelName);

        QuotaState persisted;

        if (current is null)
        {
            persisted = new QuotaState
            {
                ProviderName = modelName,
                LockedUntil = lockedUntil
            };

            context.QuotaStates.Add(persisted);
        }
        else
        {
            // Preserva a janela mais longa quando já existe bloqueio ativo para o mesmo provedor.
            if (lockedUntil <= current.LockedUntil)
            {
                return;
            }

            persisted = current with { LockedUntil = lockedUntil };
            context.QuotaStates.Update(persisted);
        }

        context.SaveChanges();

        // O AppDbContext é compartilhado por todas as operações do escopo da iteração: a instância
        // gravada é destacada para que o próximo bloqueio do mesmo provedor (portanto do mesmo Id)
        // não esbarre em uma cópia ainda rastreada no contexto.
        context.Entry(persisted).State = EntityState.Detached;
    }
}
