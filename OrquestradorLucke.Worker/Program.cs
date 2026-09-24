using OrquestradorLucke.Worker;

var builder = Host.CreateApplicationBuilder(args);

// Composição da injeção de dependência: IOptions, um expert do Google AI Studio por modelo do
// catálogo MoE, Circuit Breaker de cota (Singleton) e política de resiliência (Polly).
builder.Services.AddOrchestrator(builder.Configuration);

builder.Services.AddHostedService<LuckeOrchestratorWorker>();

var host = builder.Build();
host.Run();


