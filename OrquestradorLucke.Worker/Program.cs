using OrquestradorLucke.Worker;

var builder = Host.CreateApplicationBuilder(args);

// Daemon no Linux: habilita o Type=notify (sd_notify) e o formatter do journald. A extensão é
// context-aware — só ativa quando o processo roda sob systemd (ou NOTIFY_SOCKET no Unix), mantendo
// o lifetime de console (Ctrl+C, shutdown gracioso) em dev/execução manual.
builder.Services.AddSystemd();

// Composição da injeção de dependência: IOptions, um expert do Google AI Studio por modelo do
// catálogo MoE, Circuit Breaker de cota (Singleton) e política de resiliência (Polly).
builder.Services.AddOrchestrator(builder.Configuration);

builder.Services.AddHostedService<LuckeOrchestratorWorker>();

var host = builder.Build();
host.Run();


