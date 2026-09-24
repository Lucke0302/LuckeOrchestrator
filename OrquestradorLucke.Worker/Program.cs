using OrquestradorLucke.Worker;

var builder = Host.CreateApplicationBuilder(args);

// Daemon no Linux: habilita o Type=notify (sd_notify) e o formatter do journald. A extensão é
// context-aware — só ativa quando o processo roda sob systemd (ou NOTIFY_SOCKET no Unix), mantendo
// o lifetime de console (Ctrl+C, shutdown gracioso) em dev/execução manual.
builder.Services.AddSystemd();

// Composição da injeção de dependência: IOptions, um expert do Google AI Studio por modelo do
// catálogo MoE, o expert de embeddings do RAG, Circuit Breaker de cota (Singleton), política de
// resiliência (Polly) e a persistência PostgreSQL/pgvector.
builder.Services.AddOrchestrator(builder.Configuration);

// Valida o grafo de DI antes de subir: dependência não registrada (ou serviço Scoped consumido pela
// raiz) falha aqui, no start do daemon, em vez de aparecer só no journal da primeira iteração.
DependencyInjectionSetup.ValidateOrchestratorComposition(builder.Services);

builder.Services.AddHostedService<LuckeOrchestratorWorker>();

var host = builder.Build();
host.Run();


