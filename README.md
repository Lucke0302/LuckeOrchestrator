# Orquestrador Lucke

Agente autônomo de Engenharia de Software: um **worker service .NET 10** que consome uma **fila no
PostgreSQL**, decide qual modelo de linguagem executa cada tarefa (*Mixture of Experts*), recupera
contexto da **própria base de código** (RAG com `pgvector`), entrega o resultado como
**branch/commit/pull request** no GitHub e devolve o ciclo de vida da tarefa ao banco — sem
intervenção humana e sem clonar o repositório na máquina.

Roda como daemon (`systemd`). O **cérebro** é o roteador MoE com Circuit Breaker de cota, os
**braços** são o `GitHubAdapter` (Git Data API via Octokit, tudo em memória) e a **memória de
trabalho** é o índice vetorial da base de código.

## Sumário

- [Fluxo de uma tarefa](#fluxo-de-uma-tarefa)
- [Clean Architecture aplicada](#clean-architecture-aplicada)
- [Estratégia MoE e Circuit Breaker de cota](#estratégia-moe-e-circuit-breaker-de-cota)
- [Extração segura da resposta do modelo](#extração-segura-da-resposta-do-modelo)
- [Git Data API em memória (Octokit)](#git-data-api-em-memória-octokit)
- [RAG: contexto da própria base de código](#rag-contexto-da-própria-base-de-código)
- [Sumarização da descrição do Pull Request](#sumarização-da-descrição-do-pull-request)
- [Fila e ciclo de vida da tarefa](#fila-e-ciclo-de-vida-da-tarefa)
- [Configuração e segredos](#configuração-e-segredos)
- [Migrations (EF Core + pgvector)](#migrations-ef-core--pgvector)
- [Resiliência do daemon](#resiliência-do-daemon)
- [Como executar](#como-executar)
- [Limitações e próximos passos](#limitações-e-próximos-passos)

## Fluxo de uma tarefa

```
                 ┌───────────────── LuckeOrchestratorWorker (BackgroundService) ─────────────────┐
                 │                                                                               │
 PostgreSQL      │  1. CodebaseIndexerService ──► GitHub: árvore da branch base (só .cs)         │
 agent_tasks     │         └─ SHA-256 por arquivo ──► upsert apenas do que é novo/mudou           │
 (fila) ───────► │  2. IEmbeddingProvider (text-embedding-004) ──► pgvector: code_documents      │
   dequeue       │  3. SearchSimilarAsync(limit 3) ──► "Arquivos de referência: ..." (contexto)  │
                 │  4. ITaskRouter.ResolveProvider(complexidade) ──► expert da cadeia MoE        │
                 │  5. ILLMProvider.GenerateCodeAsync(payload, contexto) ──► artefato            │
                 │  6. IGitHubService: CreateBranch ─► Commit ─► OpenPullRequest (resumo do PR)   │
                 │  7. AgentTask with { Status = Concluida, Branch, PullRequestUrl }             │
                 └───────────────────────────────────────────────────────────────────────────────┘
```

1. **Indexação (RAG)** — a cada `Orchestrator:IndexingIntervalMinutes`, o `CodebaseIndexerService`
   lê os arquivos `.cs` da branch base via Git Data API, compara o SHA-256 do conteúdo com o
   `content_hash` gravado em `code_documents` e só embute (e grava) o que é novo ou mudou. Embedding
   consome cota, então arquivo inalterado nunca é reenviado ao provedor.
2. **Dequeue** — `IAgentTaskRepository.GetNextPendingTaskAsync` reivindica a tarefa `Pendente` mais
   antiga e a marca como `EmExecucao` em um `UPDATE` condicional: se outra instância pegar a mesma
   linha no intervalo, nenhuma linha é afetada e a reivindicação é descartada (sem processamento
   duplicado).
3. **Contexto** — o payload é embutido e os 3 documentos mais próximos (distância de cosseno `<=>`)
   são concatenados em `Arquivos de referência:`.
4. **Roteamento MoE** — o `MoETaskRouter` escolhe o primeiro expert com cota ativa na cadeia da
   complexidade; se a cadeia inteira estiver bloqueada, a tarefa **sobe** para a complexidade
   superior.
5. **Geração** — o expert devolve o artefato a partir do payload + contexto da base.
6. **Entrega** — branch `feat/task-{id}`, commit com o artefato em `Orchestrator:GeneratedArtifactPath`
   e pull request com a descrição sumarizada por um expert rápido (`TaskComplexity.Baixo`).
7. **Persistência** — a tarefa é gravada como `Concluida` com `Branch` e `PullRequestUrl`
   (o record imutável do Domain é mutado por `with`).

## Clean Architecture aplicada

```
OrquestradorLucke.Domain          → entidades, records, enums e exceções de negócio (zero NuGet)
      ▲
OrquestradorLucke.Application     → contratos (interfaces) e casos de uso; não conhece HTTP/EF
      ▲
OrquestradorLucke.Infrastructure  → EF Core/Npgsql/pgvector, Octokit, adapters do Google AI Studio
      ▲
OrquestradorLucke.Worker          → DI, IOptions e BackgroundService (host do daemon)
```

| Camada | Responsabilidade | Exemplos |
| --- | --- | --- |
| **Domain** | Regras que não dependem de tecnologia | `AgentTask`, `CodeDocument`, `TaskComplexity`, `AgentTaskStatus`, `FrustrationTracker`, `QuotaExhaustedException` |
| **Application** | Contratos e orquestração de caso de uso | `IAgentTaskRepository`, `ILLMProvider`, `IEmbeddingProvider`, `ICodeContextRepository`, `IGitHubService`, `ITaskRouter`, `IQuotaManager`, `CodebaseIndexerService` |
| **Infrastructure** | Implementações reais | `AppDbContext` (+ migrations), `AgentTaskRepository`, `CodeContextRepository`, `GoogleAiStudioAdapter`, `GitHubAdapter`, `MoETaskRouter`, `InMemoryQuotaManager`, `ModelCatalog` |
| **Worker** | Composição e laço do daemon | `Program`, `DependencyInjectionSetup`, `LuckeOrchestratorWorker`, `OrchestratorWorkerOptions` |

Regras respeitadas no código:

- o **Domain** não referencia nenhum pacote de infraestrutura (nem o `Pgvector`: o vetor é
  `ReadOnlyMemory<float>`);
- a **Application** conhece apenas os próprios contratos — inclusive o `CodebaseIndexerService`, que
  usa `IGitHubService`, `IEmbeddingProvider` e `ICodeContextRepository`;
- o **EF Core e o pgvector** existem somente na Infrastructure: o `AppDbContext`, as migrations e a
  configuração da coluna `vector(d)` ficam lá, e o **Worker não declara pacote de banco** — só
  `Microsoft.EntityFrameworkCore.Design` (`PrivateAssets=all`, para o EF CLI rodar contra o projeto
  do host), `Microsoft.Extensions.Hosting` e `Hosting.Systemd`;
- serviços **Scoped** (`AppDbContext`, repositories, adapters, clientes HTTP) são resolvidos dentro
  de um escopo criado por `IServiceScopeFactory` a cada iteração do laço — nunca injetados no
  construtor do `BackgroundService`, o que prenderia o `DbContext` ao tempo de vida do processo;
- nenhum `new HttpClient()`: os adapters recebem Named Clients do `IHttpClientFactory` com política
  Polly aplicada por nome de modelo;
- todo método assíncrono aceita e repassa `CancellationToken`, e nenhum segredo/URL/caminho está
  hardcoded: tudo vem de `IOptions<T>` (appsettings, variáveis de ambiente ou user-secrets).

## Estratégia MoE e Circuit Breaker de cota

`ModelCatalog` é a única fonte de verdade dos modelos: define as cadeias de prioridade por
complexidade e a dimensão dos vetores de embedding.

| Complexidade | Cadeia (o primeiro com cota ativa vence) |
| --- | --- |
| `Baixo` | `gemma-4-26b-a4b-it` → `gemini-3.5-flash-lite` |
| `Medio` | `gemma-4-31b-it` → `gemini-3.1-flash-lite` |
| `Alto` | `gemini-3.5-flash` → `gemini-3.6-flash` → `gemini-3.7-flash` |
| `Critico` | `gemini-3.6-flash` → `gemini-3.8-flash` → `gemini-3-flash-preview` |

- `MoETaskRouter.ResolveProvider(complexidade)` percorre a cadeia da complexidade e, se toda ela
  estiver bloqueada, sobe para a complexidade superior; a tarefa só fica sem atendimento quando
  **nenhum** modelo está ativo — caso em que lança `QuotaExhaustedException`.
- O roteador valida o catálogo na construção (falha rápido se um modelo da cadeia não tiver expert
  registrado na DI).
- `InMemoryQuotaManager` (Singleton) é o **Circuit Breaker de cota**: ao receber HTTP 429, o
  `GoogleAiStudioAdapter` bloqueia **apenas aquele modelo** (janela `AiStudio:QuotaLockoutHours`,
  24 h por padrão) e lança `QuotaExhaustedException`. A exceção **não** deriva de
  `HttpRequestException` de propósito: a política de retry não repete a chamada (repetir não
  recupera cota) e o fallback desce para o próximo modelo da cadeia.
- `FrustrationTracker` (Domain) contabiliza falhas de execução (ex.: retorno vazio) e marca
  `OverdriveDisparado` ao atingir `Frustration:MaxFailures`, sinalizando escalada para o modelo mais
  robusto (`ITaskRouter.ResolveOverdriveProvider()`).
- Política Polly (`HttpResilience`): retry com backoff exponencial para 5xx/408 e
  `HttpRequestException`; 400/401/403/404 falham sem retry.

## Extração segura da resposta do modelo

Os modelos Gemma imprimem o raciocínio (*Chain-of-Thought*) antes do payload. O
`AiStudioResponseReader` faz a leitura em duas etapas:

1. desserializa o envelope (`candidates[].content.parts[]`) e **descarta** os fragmentos marcados
   como `thought`;
2. extrai estritamente o artefato útil: bloco cercado por crases triplas, objeto/array **JSON
   balanceado** (varrendo strings e escapes, aceitando chaves e colchetes aninhados) ou o texto
   restante já sem marcas de raciocínio (`* Input:`, `- Constraint:`, tags `thinking`/`scratchpad`,
   tokens `start_of_turn`…).

Resposta sem payload devolve `string.Empty`, e o worker trata isso como falha da execução
(incrementa a frustração e marca a tarefa como `Falhou`) em vez de commitar conteúdo vazio.

## Git Data API em memória (Octokit)

O `GitHubAdapter` opera o repositório **sem clonar nada**: blobs, árvore, commit e referência são
montados como objetos e trafegam por HTTP (Git Data API). Nenhum arquivo é lido ou escrito no
sistema de arquivos local.

| Operação | Rotas utilizadas |
| --- | --- |
| Branch de trabalho | `git/refs` de `GitHub:BaseBranch` → `POST git/refs` (`feat/task-{id}`) |
| Commit | um blob por arquivo → `git/trees` com `BaseTree` (preserva o conteúdo já versionado) → `git/commits` → `PATCH git/refs` |
| Pull request | `POST pulls` (base = `GitHub:BaseBranch`), corpo = descrição sumarizada |
| Indexação do RAG | `git/trees?recursive=1` + `git/blobs`, filtrando apenas blobs `.cs` com conteúdo textual |

O Octokit 14 não expõe overloads com `CancellationToken` nas rotas de Git; por isso o adapter valida
o token antes da árvore e a cada blob baixado. Blobs acima do limite da API voltam sem base64 e são
ignorados na indexação. O token do agente autônomo (`GitHub:Token`) é obrigatório: o adapter falha
rápido na construção quando ele não está configurado.

## RAG: contexto da própria base de código

O agente não adivinha a estrutura do projeto: ele **indexa a própria base** e injeta os trechos mais
relevantes no prompt.

```
CodeDocument (Domain)             ICodeContextRepository               code_documents (PostgreSQL)
─────────────────────────         ──────────────────────────           ───────────────────────────
Id             Guid           ┌─► UpsertDocumentAsync             ┌─► id            uuid (PK)
FilePath       string         │   SearchSimilarAsync(emb, limit)  │   file_path     varchar(512) UNIQUE
ContentHash    string         │   GetAllTrackedFilesAsync         └─► content_hash  varchar(64)
Content        string         │   GetTrackedContentHashesAsync        content       text
Embedding      ReadOnlyMemory<float>                                  embedding     vector(768)
```

- **Modelo de embeddings:** `models/text-embedding-004`, em
  `POST {ApiVersion}/models/{model}:embedContent` com o corpo
  `{"model": "models/text-embedding-004", "content": {"parts": [{"text": "..."}]}}`; o vetor vem de
  `response.embedding.values`. O próprio `GoogleAiStudioAdapter` implementa `IEmbeddingProvider`
  reaproveitando o tratamento de 429/cota e falhas transitórias do `generateContent`, com Named
  Client próprio — um 429 de embedding bloqueia só o modelo de embeddings, sem tirar experts do
  rodízio MoE. Resposta sem `embedding.values` devolve vetor vazio e o documento é descartado (nunca
  se grava vetor degenerado).
- **Mapeamento do vetor:** o Domain expõe `ReadOnlyMemory<float>` e não conhece o pgvector; o
  `AppDbContext` declara a coluna `vector(ModelCatalog.EmbeddingDimensions)` = `vector(768)` com um
  conversor para `Pgvector.Vector` — o CLR type que o provider sabe mapear e parametrizar.
- **Busca por similaridade:** `OrderBy(document => document.Embedding.CosineDistance(queryVector))`
  é traduzido para SQL como `ORDER BY embedding <=> @p LIMIT n` (operador de distância de cosseno do
  pgvector). Na `Pgvector.EntityFrameworkCore` 0.3.0 `CosineDistance` é uma extensão de `object`
  (`EF.Functions.CosineDistance(a, b)` não existe nessa versão); o vetor de consulta é enviado como
  `Pgvector.Vector` para o parâmetro chegar ao banco no tipo `vector`.
- **Indexação incremental:** o indexador lista os caminhos já presentes (`GetAllTrackedFilesAsync`),
  compara os hashes gravados (`GetTrackedContentHashesAsync`) com o SHA-256 do conteúdo atual e só
  então chama o provedor de embeddings. O `UpsertDocumentAsync` procura pelo `FilePath`: se não
  existe, insere; se existe com `content_hash` diferente, regrava conteúdo/hash/embedding mantendo o
  `Id`; se o hash é igual, **não escreve nada**.
- **Uso no prompt:** o payload da tarefa é embutido, os 3 documentos mais próximos são formatados em
  `Arquivos de referência:\n\n### {caminho}\n{conteúdo}` e esse texto entra no `GenerateCodeAsync`
  como contexto. Sem índice, sem embedding ou com falha na busca, o worker apenas loga um aviso e
  segue com contexto vazio — o RAG é enriquecimento, não pré-requisito da tarefa.

## Sumarização da descrição do Pull Request

Depois de gerar o artefato, o worker resolve um expert rápido (`TaskComplexity.Baixo`) e pede:

> "Crie um resumo curto em texto puro para a descrição de um Pull Request que implementou esta
> tarefa: {payload}. O código gerado foi: {código gerado}"

A resposta vira o corpo (*body*) do pull request. Se o sumarizador falhar (cota, timeout) ou
devolver vazio, o PR usa a descrição determinística (`Entrega automática da tarefa …` + payload):
a entrega já commitada não é desfeita por causa do resumo.

## Fila e ciclo de vida da tarefa

| Status | Significado |
| --- | --- |
| `Pendente` | na fila, aguardando dequeue |
| `EmExecucao` | reivindicada pelo worker (o dequeue já grava esse status) |
| `Concluida` | artefato commitado; `Branch` e `PullRequestUrl` preenchidos |
| `Falhou` | retorno vazio do modelo ou falha não recuperável |
| `Cancelada` | interrompida por solicitação/desligamento |

O índice `ix_agent_tasks_status_criado_em` (`status`, `criado_em`) atende exatamente o dequeue:
`WHERE status = 'Pendente' ORDER BY criado_em`. A tabela é a única interface com o resto do sistema —
qualquer produtor pode inserir uma linha `Pendente` (com `payload` e `complexidade`) e o daemon a
processa sem reinício.

## Configuração e segredos

`appsettings.json` (nenhum segredo em código):

| Chave | Papel |
| --- | --- |
| `ConnectionStrings:DefaultConnection` | PostgreSQL (fila + índice vetorial) |
| `AiStudio:BaseUrl` / `ApiVersion` / `TimeoutSeconds` / `QuotaLockoutHours` | endpoint e limites do Google AI Studio |
| `AiStudio:ApiKey` | **segredo** (variável de ambiente / user-secrets) |
| `HttpResilience:RetryAttempts` / `BaseDelaySeconds` | política Polly (retry + backoff) |
| `GitHub:Token` | **segredo** do agente autônomo |
| `GitHub:Owner` / `Repository` / `BaseBranch` | repositório de trabalho e branch base |
| `Frustration:MaxFailures` | falhas toleradas antes do overdrive |
| `Orchestrator:PollingIntervalSeconds` | cadência do laço quando a fila está vazia |
| `Orchestrator:QuotaCooldownMinutes` | cooldown do laço quando toda a cadeia MoE está bloqueada |
| `Orchestrator:GeneratedArtifactPath` | caminho do arquivo entregue no commit (ex.: `Feature.cs`) |
| `Orchestrator:IndexingIntervalMinutes` | cadência do indexador do RAG |

> **Atenção (systemd/Production):** user-secrets e `appsettings.Development.json` **não** são
> carregados fora do ambiente Development. Em produção, `AiStudio:ApiKey`, `GitHub:Token`,
> `GitHub:Owner` e `GitHub:Repository` precisam vir de variáveis de ambiente do unit
> (`AiStudio__ApiKey`, `GitHub__Token`, `GitHub__Owner`, `GitHub__Repository`), por exemplo via
> `EnvironmentFile=/etc/lucke/lucke.env`.

## Migrations (EF Core + pgvector)

O EF CLI roda contra a Infrastructure através do `AppDbContextFactory` (design-time), que lê a
connection string da variável de ambiente — sem valor hardcoded:

```powershell
$env:ConnectionStrings__DefaultConnection='Host=<host>;Database=lucke_db;Username=<user>;Password=<senha>'
dotnet ef migrations add AddCodeDocuments --project OrquestradorLucke.Infrastructure --output-dir Data/Migrations
dotnet ef database update                 --project OrquestradorLucke.Infrastructure
```

Migrations existentes:

- `InitialCreate` — `CREATE EXTENSION IF NOT EXISTS vector` + tabela `agent_tasks`;
- `AddCodeDocuments` — tabela `code_documents` com `embedding vector(768) NOT NULL` e índice único
  `ux_code_documents_file_path` (chave natural do upsert).

> **Aplique `database update` antes de rodar o daemon.** Sem a tabela, a indexação e o RAG falham de
> forma degradada (a tarefa segue sem contexto, com log de aviso) — a fila continua funcionando.

## Resiliência do daemon

- **Cota esgotada** em toda a cadeia MoE: a tarefa volta para `Pendente`, o worker loga o *cooldown*
  e dorme `Orchestrator:QuotaCooldownMinutes` (15 min por padrão) para desestressar o provedor.
- **Falha genérica**: log do erro e tarefa marcada como `Falhou`.
- **Desligamento** (Ctrl+C / `systemctl stop`): a tarefa em execução volta para `Pendente` — a
  gravação usa `CancellationToken.None`, pois o token do host já está cancelado — e nada fica preso
  em `EmExecucao`.
- **Indexação e contexto do RAG** são best-effort: falha de GitHub, banco ou embeddings gera aviso e
  o laço segue com o índice atual.
- **Validação da DI no start**: `DependencyInjectionSetup.ValidateOrchestratorComposition` roda
  `ValidateOnBuild`/`ValidateScopes` antes de o host subir — dependência não registrada falha no
  start, e não na primeira iteração.

## Como executar

```powershell
# 1) Segredos (development) — nunca no appsettings
dotnet user-secrets set "AiStudio:ApiKey" "<chave>" --project OrquestradorLucke.Worker
dotnet user-secrets set "GitHub:Token"    "<token>" --project OrquestradorLucke.Worker

# 2) Banco: aplica as migrations (assume o DEFAULT_CONNECTION já configurado)
$env:ConnectionStrings__DefaultConnection='Host=<host>;Database=lucke_db;Username=<user>;Password=<senha>'
dotnet ef database update --project OrquestradorLucke.Infrastructure

# 3) Daemon
dotnet run --project OrquestradorLucke.Worker

# 4) Enfileira uma tarefa (exemplo via psql)
INSERT INTO agent_tasks (id, payload, complexidade, status, criado_em)
VALUES (gen_random_uuid(), 'Criar endpoint de cálculo de frete', 'Medio', 'Pendente', now());
```

Em produção, o deploy é o workflow `.github/workflows/deploy.yml`
(`dotnet publish -c Release` para `/var/opt/lucke-orchestrator` + `systemctl restart
lucke-orchestrator.service`). O `AddSystemd()` habilita `Type=notify`/journald automaticamente, e o
lifetime de console (Ctrl+C, shutdown gracioso) continua valendo em execução manual.

## Limitações e próximos passos

- **Um arquivo por tarefa:** `GenerateCodeAsync` devolve uma `string`; o commit publica o artefato em
  `Orchestrator:GeneratedArtifactPath`. Um formato JSON de múltiplos arquivos é a evolução natural.
- **Busca vetorial exata:** o `code_documents` usa *seq scan* ordenado por `<=>`. Para bases grandes,
  um índice aproximado (`hnsw`/`ivfflat` com `vector_cosine_ops`) resolve a latência.
- **Overdrive:** `FrustrationTracker` e `ResolveOverdriveProvider()` já existem, mas a tarefa é
  marcada como `Falhou` no ciclo; a retentativa escalonada no modelo mais robusto é o próximo passo.
- **Idempotência de entrega:** um desligamento entre o commit e a abertura do PR devolve a tarefa
  para `Pendente`; no ciclo seguinte a branch já existiria (tratar com `409 Conflict`).
- **Indexação sob demanda:** hoje o indexador roda por intervalo; um gancho pós-merge (webhook)
  deixaria o índice quente com menos chamadas à API do GitHub.
- **Testes automatizados:** não há projeto de testes na solução (Domain puro e adapters preparam
  facilmente um projeto `xUnit`).




