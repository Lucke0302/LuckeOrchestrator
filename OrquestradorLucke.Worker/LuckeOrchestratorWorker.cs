using Microsoft.Extensions.Options;
using OrquestradorLucke.Application.Interfaces;
using OrquestradorLucke.Worker.Configuration;

namespace OrquestradorLucke.Worker;

/// <summary>
/// Laço principal do orquestrador. Cada iteração abre um escopo próprio de DI para que
/// nenhuma dependência (DbContext, adapters, Typed Clients) carregue estado entre execuções.
/// </summary>
public sealed class LuckeOrchestratorWorker(IServiceScopeFactory scopeFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = scopeFactory.CreateScope();

            // Resolve o roteador (e, por consequência, os experts) dentro do escopo da iteração.
            _ = scope.ServiceProvider.GetRequiredService<ITaskRouter>();

            var options = scope.ServiceProvider.GetRequiredService<IOptions<OrchestratorWorkerOptions>>().Value;

            // TODO: substituir pelo caso de uso de orquestração
            // (buscar tarefa -> classificar complexidade -> rotear -> executar -> commit/PR).

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.PollingIntervalSeconds)), stoppingToken)
                .ConfigureAwait(false);
        }
    }
}
