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
- [Overdrive: escalada para o modelo mais robusto](#overdrive-escalada-para-o-modelo-mais-robusto)
- [Extração segura da resposta do modelo](#extração-segura-da-resposta-do-modelo)
- [Git Data API em memória (Octokit)](#git-data-api-em-memória-octokit)
- [RAG: contexto da própria base de código](#rag-contexto-da-própria-base-de-código)
- [Sumarização da descrição do Pull Request](#sumarização-da-descrição-do-pull-request)
- [Fila e ciclo de vida da tarefa](#fila-e-ciclo-de-vida-da-tarefa)
- [Host HTTP e webhook do GitHub](#host-http-e-webhook-do-github)
- [Configuração e segredos](#configuração-e-segredos)
- [Migrations (EF Core + pgvector)](#migrations-ef-core--pgvector)
- [Resiliência do daemon](#resiliência-do-daemon)
- [Como executar](#como-executar)
- [Testes automatizados](#testes-automatizados)
- [Limitações e próximos passos](#limitações-e-próximos-passos)

## Fluxo de uma tarefa

```
                 ┌───────────────── LuckeOrchestratorWorker (BackgroundService) ─────────────────┐
                 │                                                                               │
 PostgreSQL      │  1. CodebaseIndexerService ──► GitHub: árvore da branch base (só .cs)         │
 agent_tasks     │         └─ SHA-256 por arquivo ──► upsert apenas do que é novo/mudou           │
 (fila) ───────► │  2. IEmbeddingProvider (gemini-embedding-2) ──► pgvector: code_documents      │
   dequeue       │  3. SearchSimilarAsync(limit 3) ──► "Arquivos de referência: ..." (contexto)  │
                 │  4. ITaskRouter.ResolveProvider(complexidade) ──► expert da cadeia MoE        │
                 │  5. ILLMProvider.GenerateCodeAsync(payload, contexto) ──► arquivos (JSON)     │
                 │  6. IGitHubService: CreateBranch ─► Commit ─► OpenPullRequest (resumo do PR)   │
                 │  7. AgentTask with { Status = Concluida, Branch, PullRequestUrl }             │
                 └───────────────────────────────────────────────────────────────────────────────┘
```

O host também expõe `POST /api/webhook/github`, que dispara a indexação do RAG sob demanda (ver
[Host HTTP e webhook do GitHub](#host-http-e-webhook-do-github)).

1. **Indexação (RAG)** — a cada `Orchestrator:IndexingIntervalMinutes` ou a cada webhook do GitHub
   (coalescido pelo `IndexingChannel` e executado por um único consumidor), o `CodebaseIndexerService`
   lê os arquivos `.cs` da branch base via Git Data API, compara o SHA-256 do conteúdo com o
   `content_hash` gravado em `code_documents` e só embute (e grava) o que é novo ou mudou — ao final,
   remove do índice os documentos que não existem mais na árvore. Embedding consome cota, então arquivo
   inalterado nunca é reenviado ao provedor.
2. **Dequeue** — `IAgentTaskRepository.GetNextPendingTaskAsync` reivindica a tarefa `Pendente` mais
   antiga e a marca como `EmExecucao` em um `UPDATE` condicional: se outra instância pegar a mesma
   linha no intervalo, nenhuma linha é afetada e a reivindicação é descartada (sem processamento
   duplicado).
3. **Contexto** — o payload é embutido e os 3 documentos mais próximos (distância de cosseno `<=>`)
   são concatenados em `Arquivos de referência:`.
4. **Roteamento MoE** — o `MoETaskRouter` escolhe o primeiro expert com cota ativa na cadeia da
   complexidade; se a cadeia inteira estiver bloqueada, a tarefa **sobe** para a complexidade
   superior.
5. **Geração** — o expert devolve um objeto **JSON estrito** `{"caminho/do/arquivo.cs": "conteúdo"}`
   (um ou mais arquivos) a partir do payload + contexto da base. Resposta fora desse formato é
   convertida em falha e alimenta a mecânica de frustração (ver
   [Overdrive](#overdrive-escalada-para-o-modelo-mais-robusto)).
6. **Entrega** — branch `feat/task-{id}`, commit com **todos** os arquivos do dicionário (sem caminho
   fixo de artefato) e pull request com a descrição sumarizada por um expert rápido
   (`TaskComplexity.Baixo`).
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
| **Domain** | Regras que não dependem de tecnologia | `AgentTask`, `CodeDocument`, `TaskComplexity`, `AgentTaskStatus`, `FrustrationTracker`, `QuotaExhaustedException`, `QuotaState` |
| **Application** | Contratos e orquestração de caso de uso | `IAgentTaskRepository`, `ILLMProvider`, `IEmbeddingProvider`, `ICodeContextRepository`, `IGitHubService`, `ITaskRouter`, `IQuotaManager`, `CodebaseIndexerService` |
| **Infrastructure** | Implementações reais | `AppDbContext` (+ migrations), `AgentTaskRepository`, `CodeContextRepository`, `GoogleAiStudioAdapter`, `GitHubAdapter`, `MoETaskRouter`, `DbQuotaManager`, `ModelCatalog` |
| **Worker** | Composição e laço do daemon | `Program` (host HTTP + webhook), `DependencyInjectionSetup`, `LuckeOrchestratorWorker`, `OrchestratorWorkerOptions`, `IndexingChannel`, `IndexingBackgroundService` |
| **Tests** | Testes automatizados (xUnit + Moq + FluentAssertions) | `MoETaskRouterTests`, `FrustrationTrackerTests`, `CodebaseIndexerServiceTests`, `OrchestratorCompositionTests` |

Regras respeitadas no código:

- o **Domain** não referencia nenhum pacote de infraestrutura (nem o `Pgvector`: o vetor é
  `ReadOnlyMemory<float>`);
- a **Application** conhece apenas os próprios contratos — inclusive o `CodebaseIndexerService`, que
  usa `IGitHubService`, `IEmbeddingProvider` e `ICodeContextRepository`;
- o **EF Core e o pgvector** existem somente na Infrastructure: o `AppDbContext`, as migrations e a
  configuração da coluna `vector(d)` ficam lá, e o **Worker não declara pacote de banco** — só
  `Microsoft.EntityFrameworkCore.Design` (`PrivateAssets=all`, para o EF CLI rodar contra o projeto do
  host) e `Hosting.Systemd`. O host web vem de `<FrameworkReference Include="Microsoft.AspNetCore.App" />`
  (referência de framework, não pacote), que já traz `Microsoft.Extensions.Hosting`;
- serviços **Scoped** (`AppDbContext`, repositories, adapters, clientes HTTP) são resolvidos dentro
  de um escopo criado por `IServiceScopeFactory` a cada iteração do laço — nunca injetados no
  construtor do `BackgroundService`, o que prenderia o `DbContext` ao tempo de vida do processo;
- nenhum `new HttpClient()`: os adapters recebem Named Clients do `IHttpClientFactory` com política
  Polly aplicada por nome de modelo;
- todo método assíncrono aceita e repassa `CancellationToken`, e nenhum segredo/URL/caminho está
  hardcoded: tudo vem de `IOptions<T>` (appsettings, variáveis de ambiente ou user-secrets). O único
  valor lido direto de `IConfiguration` é `GitHub:WebhookSecret`, no endpoint do webhook — a validação
  do HMAC acontece antes de qualquer serviço do container, mas a fonte é a mesma (variável de ambiente
  ou user-secrets).

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
- `DbQuotaManager` (**Scoped**, tabela `quota_states`) é o **Circuit Breaker de cota**: ao receber
  HTTP 429, o `GoogleAiStudioAdapter` bloqueia **apenas aquele modelo** (janela
  `AiStudio:QuotaLockoutHours`, 24 h por padrão) e lança `QuotaExhaustedException`. A exceção **não**
  deriva de `HttpRequestException` de propósito: a política de retry não repete a chamada (repetir não
  recupera cota) e o fallback desce para o próximo modelo da cadeia.
- O bloqueio é **persistido no PostgreSQL** (`QuotaState`: `ProviderName` único + `LockedUntil`), e não
  em memória: reiniciar o daemon **não** devolve ao rodízio um modelo com cota esgotada, e todas as
  instâncias que apontam para o mesmo banco compartilham o mesmo Circuit Breaker. Como o contrato
  `IQuotaManager` é síncrono (consumido pelo roteador e pelos adapters), a implementação usa as APIs
  síncronas do EF Core — sem `sync-over-async`. A janela expirada é removida na primeira consulta
  (purge oportunista) e a janela mais longa é preservada em bloqueios repetidos.
- `InMemoryQuotaManager` continua no projeto como implementação em memória usada pelos testes
  unitários de roteamento (sem banco).
- `FrustrationTracker` (Domain) contabiliza as falhas de geração do daemon e marca
  `OverdriveDisparado` ao atingir `Frustration:MaxFailures` — é o gatilho do overdrive descrito
  abaixo.
- Política Polly (`HttpResilience`): retry com backoff exponencial para 5xx/408 e
  `HttpRequestException`; 400/401/403/404 falham sem retry.

## Overdrive: escalada para o modelo mais robusto

A mecânica de frustração é o que impede o daemon de insistir em um expert que não entrega:

1. cada tentativa de geração que falha — exceção do provedor (ex.: resposta que não é o JSON estrito
   de arquivos) ou retorno sem nenhum arquivo — incrementa o `FrustrationTracker`
   (`Frustration:MaxFailures`, 3 por padrão);
2. enquanto o limite **não** é atingido, a tarefa é encerrada como `Falhou` neste ciclo (comportamento
   anterior, preservado);
3. ao atingir o limite o circuito **desarma**: o laço loga `Ativando Overdrive`, resolve o expert mais
   robusto com `ITaskRouter.ResolveOverdriveProvider()` (cadeia `Critico`, com o fallback interno
   dela) e faz **uma última tentativa** de geração;
4. só se essa tentativa também falhar a tarefa é marcada como `Falhou`.

### Memória da frustração (o que o modelo maior recebe)

Cada falha também guarda o **motivo** em `FrustrationTracker.HistoricoFalhas` (modelo + razão +
mensagem da exceção, ex.: `models/gemma-4-26b-a4b-it: retorno sem arquivos`). Ao escalar, o worker
formata esse histórico e o **concatena ao contexto do RAG** entregue ao `GenerateCodeAsync`:

```
Arquivos de referência:

### src/Caminho/Arquivo.cs
{conteúdo}

ATENÇÃO: Tentativas anteriores falharam. Evite os seguintes erros:
- models/gemma-4-26b-a4b-it: retorno sem arquivos
- models/gemma-4-26b-a4b-it: resposta não parseável (O modelo não devolveu o JSON estrito de arquivos esperado.)
```

Assim o modelo mais robusto sabe exatamente o que o modelo menor errou e não repete o erro. O
histórico mantém no máximo 10 entradas (as mais recentes — o prompt não precisa de mais que isso e a
memória fica limitada em um daemon de longa duração) e é **limpo no primeiro sucesso**
(`RegistrarSucesso`), junto do contador.

O medidor vive no `LuckeOrchestratorWorker` (campo do `BackgroundService`), e **não** no escopo da
iteração: cada tarefa tem uma única tentativa por ciclo, logo um contador local nunca alcançaria o
limite e o overdrive jamais dispararia. Ele é **zerado no primeiro sucesso** (`RegistrarSucesso`), o
que rearma o circuito.

`QuotaExhaustedException` **não** conta como frustração: cota esgotada é tratada pelo Circuit Breaker
(devolve a tarefa para a fila + *cooldown*) e não consome tentativa do overdrive. O mesmo vale para o
desligamento do host, que devolve a tarefa para `Pendente`.

## Extração segura da resposta do modelo

Os modelos Gemma imprimem o raciocínio (*Chain-of-Thought*) antes do payload. O
`AiStudioResponseReader` faz a leitura em duas etapas:

1. desserializa o envelope (`candidates[].content.parts[]`) e **descarta** os fragmentos marcados
   como `thought`;
2. extrai estritamente o artefato útil: bloco cercado por crases triplas, objeto/array **JSON
   balanceado** (varrendo strings e escapes, aceitando chaves e colchetes aninhados) ou o texto
   restante já sem marcas de raciocínio (`* Input:`, `- Constraint:`, tags `thinking`/`scratchpad`,
   tokens `start_of_turn`…).

Resposta sem payload devolve dicionário vazio e o worker trata isso como falha da execução
(incrementa a frustração em vez de commitar arquivo vazio).

### Contrato de múltiplos arquivos

O prompt de `GenerateCodeAsync` fixa o formato da resposta: um objeto JSON estrito, sem texto antes ou
depois.

```json
{ "src/Caminho/Arquivo.cs": "conteúdo completo do arquivo" }
```

- a **chave** é o caminho relativo do arquivo no repositório (a barra invertida do Windows é
  normalizada para `/` e a barra inicial é removida — caminho absoluto não é versionável);
- o **valor** é o conteúdo completo daquele arquivo;
- uma chave por arquivo necessário: a tarefa pode entregar quantos arquivos precisar.

Depois da extração do Chain-of-Thought, esse JSON é desserializado em `Dictionary<string, string>` e
segue direto para `CommitChangesAsync` — não existe mais caminho fixo de artefato. Se houver payload
mas ele **não** for esse objeto JSON (chave e valor strings) ou não trouxer arquivo nenhum, o adaptador
lança `InvalidOperationException`: a exceção é a interface da falha com a mecânica de frustração e
leva à escalada de modelo (overdrive ao atingir o limite).

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

### Idempotência da entrega

Reprocessar uma tarefa (desligamento do daemon entre o commit e a abertura do PR, nova tentativa,
reentrega manual) não pode falhar por causa do que já foi criado. As duas operações que a API do
GitHub recusa com `422` são tratadas como "já existe":

| Operação | Quando já existe | Comportamento |
| --- | --- | --- |
| `CreateBranchAsync` | branch `feat/task-{id}` já criada (`POST git/refs` → 422) | lê a referência existente (`GET git/ref`), loga e devolve o `ref` dela — o fluxo segue de onde estava |
| `OpenPullRequestAsync` | PR aberto para a mesma branch (`POST pulls` → 422) | busca o PR com `GET pulls?state=open&head={owner}:{branch}`, confere o `head.ref` exato e devolve a URL dele |

Em ambos os casos, se a validação falhar por **outro** motivo (nome inválido, permissão, PR entre
forks), a exceção original é propagada — a idempotência não mascara erros legítimos.

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
                              └─► DeleteOrphanDocumentsAsync(paths)
```

- **Modelo de embeddings:** `models/gemini-embedding-2` (o `models/text-embedding-004` foi
  descontinuado), em `POST {ApiVersion}/models/{model}:embedContent` com o corpo
  `{"model": "models/gemini-embedding-2", "content": {"parts": [{"text": "..."}]}, "outputDimensionality": 768}`;
  o vetor vem de `response.embedding.values`. O próprio `GoogleAiStudioAdapter` implementa
  `IEmbeddingProvider` reaproveitando o tratamento de 429/cota e falhas transitórias do
  `generateContent`, com Named Client próprio — um 429 de embedding bloqueia só o modelo de
  embeddings, sem tirar experts do rodízio MoE. Resposta sem `embedding.values` devolve vetor vazio e
  o documento é descartado (nunca se grava vetor degenerado).
- **Dimensão da requisição:** o `gemini-embedding-2` devolve **3072 dimensões por padrão**, mas a
  coluna `embedding` é `vector(768)` — por isso o `outputDimensionality` (truncamento Matryoshka) é
  enviado na raiz do corpo, com o valor de `ModelCatalog.EmbeddingDimensions`. A constante é a mesma
  que declara a coluna (`AppDbContext`), então requisição e banco não podem divergir; um vetor de
  3072 posições seria recusado pelo pgvector na gravação.
- **Troca de modelo exige reindexação:** os vetores já gravados em `code_documents` foram produzidos
  pelo `text-embedding-004` e vivem em **outro espaço vetorial** — mesma dimensão (`768`) não torna as
  distâncias comparáveis. Como a indexação incremental decide por `content_hash` (que não muda sozinho),
  a troca de modelo **não** reindexa: rode `TRUNCATE TABLE code_documents;` para que o próximo ciclo
  regenere o índice com o `gemini-embedding-2`.
- **Mapeamento do vetor:** o Domain expõe `ReadOnlyMemory<float>` e não conhece o pgvector; o
  `AppDbContext` declara a coluna `vector(ModelCatalog.EmbeddingDimensions)` = `vector(768)` com um
  conversor para `Pgvector.Vector` — o CLR type que o provider sabe mapear e parametrizar.
- **Busca por similaridade:** `OrderBy(document => document.Embedding.CosineDistance(queryVector))`
  é traduzido para SQL como `ORDER BY embedding <=> @p LIMIT n` (operador de distância de cosseno do
  pgvector). Na `Pgvector.EntityFrameworkCore` 0.3.0 `CosineDistance` é uma extensão de `object`
  (`EF.Functions.CosineDistance(a, b)` não existe nessa versão); o vetor de consulta é enviado como
  `Pgvector.Vector` para o parâmetro chegar ao banco no tipo `vector`.
- **Índice vetorial aproximado (HNSW):** a coluna `embedding` tem o índice
  `ix_code_documents_embedding_hnsw` com `USING hnsw (embedding vector_cosine_ops)` — a mesma classe de
  operadores da consulta (`<=>`), condição para o planner usar o índice em vez de um *seq scan*
  ordenado por distância. Sem ele a busca degrada linearmente com o tamanho do índice; com ele a
  latência se mantém estável em bases grandes. Os parâmetros do HNSW (`m`, `ef_construction`) ficam no
  default do pgvector.
- **Indexação incremental:** o indexador lista os caminhos já presentes (`GetAllTrackedFilesAsync`),
  compara os hashes gravados (`GetTrackedContentHashesAsync`) com o SHA-256 do conteúdo atual e só
  então chama o provedor de embeddings. O `UpsertDocumentAsync` procura pelo `FilePath`: se não
  existe, insere; se existe com `content_hash` diferente, regrava conteúdo/hash/embedding mantendo o
  `Id`; se o hash é igual, **não escreve nada**.
- **Faxina do índice (documentos órfãos):** o upsert não remove nada, então ao fim de cada
  sincronização o indexador chama `DeleteOrphanDocumentsAsync` com as chaves da árvore lida do GitHub.
  O repositório emite **um único** `DELETE ... WHERE NOT (file_path = ANY(@activePaths))` com
  `ExecuteDelete` (sem materializar os órfãos, que carregam conteúdo e vetor) — um arquivo **deletado
  ou renomeado** na branch base deixa de ser recuperado como referência. Árvore vazia (leitura
  degradada do GitHub) **não** dispara a faxina: ela nunca pode ser interpretada como "todos os
  arquivos foram removidos".
- **Uso no prompt:** o payload da tarefa é embutido, os 3 documentos mais próximos são formatados em
  `Arquivos de referência:\n\n### {caminho}\n{conteúdo}` e esse texto entra no `GenerateCodeAsync`
  como contexto. Sem índice, sem embedding ou com falha na busca, o worker apenas loga um aviso e
  segue com contexto vazio — o RAG é enriquecimento, não pré-requisito da tarefa.

## Sumarização da descrição do Pull Request

Depois de gerar os arquivos, o worker resolve um expert rápido (`TaskComplexity.Baixo`) e pede:

> "Crie um resumo curto em texto puro para a descrição de um Pull Request que implementou esta
> tarefa: {payload}. Arquivos gerados: ### {caminho} {conteúdo}"

O conteúdo de cada arquivo entra limitado a 2000 caracteres (o resumo descreve a mudança, não o código
inteiro, e um prompt gigante só consumiria contexto e cota). O expert responde sob o mesmo contrato
JSON da geração, então o resumo é o texto útil devolvido — concatenado quando vem distribuído em mais
de um valor.

A resposta vira o corpo (*body*) do pull request. Se o sumarizador falhar (cota, timeout), devolver
vazio ou devolver algo que não é o JSON esperado, o PR usa a descrição determinística (`Entrega
automática da tarefa …` + payload): a entrega já commitada não é desfeita por causa do resumo.

## Fila e ciclo de vida da tarefa

| Status | Significado |
| --- | --- |
| `Pendente` | na fila, aguardando dequeue |
| `EmExecucao` | reivindicada pelo worker (o dequeue já grava esse status) |
| `Concluida` | arquivos commitados e PR aberto; `Branch` e `PullRequestUrl` preenchidos |
| `Falhou` | geração sem arquivos, resposta fora do JSON estrito ou falha não recuperável (após a tentativa do overdrive, quando disparado) |
| `Cancelada` | interrompida por solicitação/desligamento |

O índice `ix_agent_tasks_status_criado_em` (`status`, `criado_em`) atende exatamente o dequeue:
`WHERE status = 'Pendente' ORDER BY criado_em`. A tabela é a única interface com o resto do sistema —
qualquer produtor pode inserir uma linha `Pendente` (com `payload` e `complexidade`) e o daemon a
processa sem reinício.

## Host HTTP e webhook do GitHub

O host é criado com `WebApplication.CreateBuilder(args)` (Kestrel + Minimal APIs) **sem deixar de ser
um worker**: o `LuckeOrchestratorWorker` continua registrado como `BackgroundService` e o laço da fila
roda normalmente.

```csharp
app.MapPost("/api/webhook/github", async (HttpContext context, IConfiguration configuration,
    IndexingChannel indexingChannel, ILoggerFactory loggerFactory) =>
{
    // 1) lê o segredo (GitHub:WebhookSecret) e o corpo BRUTO da requisição (bytes, sem recodificar);
    // 2) valida o HMAC-SHA256 de X-Hub-Signature-256 contra esse corpo: divergiu ⇒ 401;
    // 3) confere ⇒ publica o gatilho no IndexingChannel (debounce) e responde 202.
});
```

- **Assinatura obrigatória (HMAC-SHA256):** o corpo bruto é lido como **bytes** — ler como string e
  recodificar poderia alterar o payload (BOM, normalização de quebras de linha) e invalidar uma
  entrega legítima. O `HMACSHA256` desse corpo é comparado ao valor de `X-Hub-Signature-256`
  (`sha256=<hex>`) com `CryptographicOperations.FixedTimeEquals` (comparação em tempo constante, que
  não vaza a assinatura por diferença de tempo), usando o segredo `GitHub:WebhookSecret`.
  Assinatura ausente/divergente ⇒ **401**; segredo não configurado ⇒ **500** (fail closed: sem segredo
  não há como autenticar a entrega).
- **`202 Accepted` imediato** — o GitHub cancela entregas que passam de ~10 s, e a indexação completa
  (árvore do repositório + embeddings) pode levar bem mais que isso.
- **Debounce com `IndexingChannel`:** o endpoint apenas faz `TryWrite(true)` em um canal Singleton
  (`Channel<bool>` com `BoundedChannelOptions(1) { FullMode = DropWrite }`). Uma rajada de webhooks
  deixa **um** pedido pendente e descarta os excedentes — a indexação pendente já cobre a mudança que
  eles sinalizariam.
- **Uma indexação por vez:** o `IndexingBackgroundService` consome o canal (`ReadAllAsync`) e roda o
  `CodebaseIndexerService` em um **escopo próprio** (`IServiceScopeFactory`, porque o indexador e o
  `DbContext` são Scoped) por pedido. O PostgreSQL recebe uma conexão por vez, em vez de N indexações
  concorrentes disputando o índice vetorial.
- falha na indexação é absorvida com log (`IndexingBackgroundService`; as recusas do webhook usam a
  categoria `OrquestradorLucke.Webhook.GitHub`): o índice atual continua servindo de contexto e o
  próximo ciclo reindexa o que faltou.
- com o webhook configurado, `Orchestrator:IndexingIntervalMinutes` deixa de ser o gatilho principal e
  passa a ser a rede de segurança contra eventos perdidos.

Endereço e porta vêm do host (Kestrel), não do código: defina `ASPNETCORE_URLS`
(ex.: `ASPNETCORE_URLS=http://127.0.0.1:5080`) no unit do systemd — sem isso o Kestrel usa o default.

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
| `GitHub:WebhookSecret` | **segredo** do HMAC-SHA256 do webhook (variável de ambiente / user-secrets) |
| `Frustration:MaxFailures` | falhas toleradas antes do overdrive (alimenta o histórico entregue ao overdrive) |
| `Orchestrator:PollingIntervalSeconds` | cadência do laço quando a fila está vazia |
| `Orchestrator:QuotaCooldownMinutes` | cooldown do laço quando toda a cadeia MoE está bloqueada |
| `Orchestrator:IndexingIntervalMinutes` | cadência (rede de segurança) do indexador do RAG |
| `ASPNETCORE_URLS` | endereço/porta do host HTTP que atende o webhook (default do Kestrel quando omitido) |

> **Atenção (systemd/Production):** user-secrets e `appsettings.Development.json` **não** são
> carregados fora do ambiente Development. Em produção, `AiStudio:ApiKey`, `GitHub:Token`,
> `GitHub:WebhookSecret`, `GitHub:Owner` e `GitHub:Repository` precisam vir de variáveis de ambiente do
> unit (`AiStudio__ApiKey`, `GitHub__Token`, `GitHub__WebhookSecret`, `GitHub__Owner`,
> `GitHub__Repository`), por exemplo via `EnvironmentFile=/etc/lucke/lucke.env`.

## Migrations (EF Core + pgvector)

O EF CLI (`dotnet-ef` 10.0.4, mesma versão do pacote EF Core) roda contra a Infrastructure através do
`AppDbContextFactory` (design-time), que lê a connection string da variável de ambiente — sem valor
hardcoded:

```powershell
dotnet tool update --global dotnet-ef --version 10.0.4

$env:ConnectionStrings__DefaultConnection='Host=<host>;Database=lucke_db;Username=<user>;Password=<senha>'

# Cria a migration do índice vetorial aproximado
dotnet ef migrations add AddHnswIndex --project OrquestradorLucke.Infrastructure --output-dir Data/Migrations

# Cria a migration do Circuit Breaker de cota persistido
dotnet ef migrations add AddQuotaState --project OrquestradorLucke.Infrastructure --output-dir Data/Migrations

# Aplica no banco (idempotente por histórico de migrations)
dotnet ef database update --project OrquestradorLucke.Infrastructure

# Confere o que falta aplicar e o SQL que será emitido
dotnet ef migrations list --project OrquestradorLucke.Infrastructure
dotnet ef migrations script 20260924180507_AddCodeDocuments 20260924182849_AddHnswIndex `
    --project OrquestradorLucke.Infrastructure -o hnsw.sql
```

Migrations existentes:

- `InitialCreate` — `CREATE EXTENSION IF NOT EXISTS vector` + tabela `agent_tasks`;
- `AddCodeDocuments` — tabela `code_documents` com `embedding vector(768) NOT NULL` e índice único
  `ux_code_documents_file_path` (chave natural do upsert);
- `AddHnswIndex` — índice vetorial aproximado da coluna de embedding:

  ```sql
  CREATE INDEX ix_code_documents_embedding_hnsw
      ON code_documents USING hnsw (embedding vector_cosine_ops);
  ```

  O `USING hnsw` transforma a busca por similaridade de um *seq scan* ordenado por `<=>` em uma busca
  aproximada por grafo; o `vector_cosine_ops` é obrigatório para o planner aceitar o índice na consulta
  `ORDER BY embedding <=> @p` feita por `SearchSimilarAsync`. A `Down` da migration derruba apenas o
  índice — nenhum dado é afetado.
- `AddQuotaState` — tabela `quota_states` (Circuit Breaker de cota persistido):

  ```sql
  CREATE TABLE quota_states (
      id uuid NOT NULL CONSTRAINT pk_quota_states PRIMARY KEY,
      provider_name character varying(255) NOT NULL,
      locked_until timestamp with time zone NOT NULL
  );
  CREATE UNIQUE INDEX ux_quota_states_provider_name ON quota_states (provider_name);
  ```

  `provider_name` é a chave natural do bloqueio (o nome do modelo no catálogo) e `locked_until` o
  instante em que ele volta ao rodízio. Como o estado vive no banco, um HTTP 429 continua valendo
  depois de um reinício do daemon e é compartilhado por todas as instâncias que apontam para o mesmo
  PostgreSQL. A `Down` derruba a tabela (o Circuit Breaker volta a se comportar como se nenhum modelo
  estivesse bloqueado).

> **Aplique `database update` antes de rodar o daemon.** Sem a tabela `code_documents`, a indexação e
> o RAG falham de forma degradada (a tarefa segue sem contexto, com log de aviso) — a fila continua
> funcionando. Sem `quota_states`, o Circuit Breaker de cota falha na primeira consulta e a tarefa é
> encerrada como `Falhou` naquele ciclo; aplique a migration junto com o deploy.

## Resiliência do daemon

- **Cota esgotada** em toda a cadeia MoE: a tarefa volta para `Pendente`, o worker loga o *cooldown*
  e dorme `Orchestrator:QuotaCooldownMinutes` (15 min por padrão) para desestressar o provedor. O
  bloqueio individual de cada modelo que respondeu 429 fica gravado em `quota_states`, então o
  próximo ciclo já começa sabendo quem está fora do rodízio.
- **Falha de geração**: incrementa a frustração e marca a tarefa como `Falhou`; ao atingir
  `Frustration:MaxFailures` o laço **ativa o overdrive** e faz uma última tentativa no modelo mais
  robusto antes de desistir (ver [Overdrive](#overdrive-escalada-para-o-modelo-mais-robusto)).
- **Reentrega idempotente**: branch e PR já existentes são reutilizados, então reprocessar uma tarefa
  não falha por causa do que já foi entregue.
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
dotnet user-secrets set "AiStudio:ApiKey"       "<chave>"   --project OrquestradorLucke.Worker
dotnet user-secrets set "GitHub:Token"          "<token>"   --project OrquestradorLucke.Worker
dotnet user-secrets set "GitHub:WebhookSecret"  "<segredo>" --project OrquestradorLucke.Worker

# 2) Banco: aplica as migrations (assume o DEFAULT_CONNECTION já configurado)
$env:ConnectionStrings__DefaultConnection='Host=<host>;Database=lucke_db;Username=<user>;Password=<senha>'
dotnet ef database update --project OrquestradorLucke.Infrastructure

# 3) Daemon (o host sobe o laço do orquestrador e o host HTTP do webhook)
dotnet run --project OrquestradorLucke.Worker

# 4) Enfileira uma tarefa (exemplo via psql)
INSERT INTO agent_tasks (id, payload, complexidade, status, criado_em)
VALUES (gen_random_uuid(), 'Criar endpoint de cálculo de frete', 'Medio', 'Pendente', now());

# 5) Webhook do GitHub assinado (401 sem assinatura válida; 202 quando o gatilho é enfileirado)
$body      = '{"ref":"refs/heads/main"}'
$secret    = '<segredo>'
$mac       = [System.Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($secret))
$signature = 'sha256=' + [Convert]::ToHexString($mac.ComputeHash([Text.Encoding]::UTF8.GetBytes($body))).ToLowerInvariant()
Invoke-WebRequest -Method Post http://localhost:5000/api/webhook/github `
    -Body $body -ContentType 'application/json' -Headers @{ 'X-Hub-Signature-256' = $signature }
```

Para rodar a suíte de testes: `dotnet test` (ou por projeto, como em
[Testes automatizados](#testes-automatizados)).

Em produção, o deploy é o workflow `.github/workflows/deploy.yml`
(`dotnet publish -c Release` para `/var/opt/lucke-orchestrator` + `systemctl restart
lucke-orchestrator.service`). O `AddSystemd()` habilita `Type=notify`/journald automaticamente, e o
lifetime de console (Ctrl+C, shutdown gracioso) continua valendo em execução manual.

> **Runtime em produção:** como o host passou a ser um `WebApplication`, o servidor precisa do
> **runtime do ASP.NET Core** (`Microsoft.AspNetCore.App` 10) — já incluído no SDK 10 — além do runtime
> base, e o unit do systemd precisa liberar a porta definida em `ASPNETCORE_URLS`.

## Testes automatizados

Projeto `OrquestradorLucke.Tests` (xUnit, Moq e FluentAssertions 7.2 — a linha 7.x permanece
Apache-2.0, enquanto a 8.x passou para a licença Xceed), referenciando `Domain`, `Application` e
`Infrastructure`:

```powershell
dotnet test OrquestradorLucke.Tests\OrquestradorLucke.Tests.csproj
dotnet test OrquestradorLucke.slnx          # solução completa
```

`MoETaskRouterTests` cobre o roteamento MoE com o `InMemoryQuotaManager` real e experts mockados (o
roteador só consulta `ModelName`), derivando as expectativas do próprio `ModelCatalog` para não
duplicar a ordem das cadeias:

- a cadeia de cada complexidade é devolvida na ordem de prioridade (a cada bloqueio de cota, o próximo
  modelo da cadeia assume);
- cadeia inteira bloqueada ⇒ a tarefa **sobe** para a complexidade superior;
- todos os modelos bloqueados ⇒ `QuotaExhaustedException`;
- `ResolveOverdriveProvider` devolve o cabeça da cadeia `Critico` e respeita o fallback dela;
- modelo do catálogo sem expert registrado ⇒ falha rápida na construção do roteador.

Os demais testes cobrem as melhorias arquiteturais:

- `FrustrationTrackerTests` — disparo do overdrive exatamente no limite, acúmulo do motivo em
  `HistoricoFalhas`, teto de 10 entradas e limpeza (contador + histórico) no primeiro sucesso;
- `CodebaseIndexerServiceTests` — arquivo com o mesmo hash não consome embedding, documento sem vetor
  é descartado (e não escrito), a faxina de órfãos recebe os caminhos da árvore atual e **não** roda
  em árvore vazia;
- `OrchestratorCompositionTests` — executa a mesma validação de DI que o host faz no start
  (`ValidateOnBuild`/`ValidateScopes`) sobre a composição real, além de fixar os tempos de vida de
  `DbQuotaManager` (Scoped) e `IndexingChannel` (Singleton) e o fail fast da connection string.

## Limitações e próximos passos

- **Migrations no deploy:** o workflow publica o binário mas não roda `dotnet ef database update`;
  aplicar as migrations no pipeline (ou versionar o script idempotente) fecha a lacuna entre código e
  banco. Sem a tabela `quota_states`, o Circuit Breaker de cota falha na primeira consulta — o worker
  loga o erro e a tarefa é encerrada como `Falhou` naquele ciclo.
- **Cooldown global de cota:** quando toda a cadeia está bloqueada o laço dorme
  `Orchestrator:QuotaCooldownMinutes` sem distinguir modelos; um agendamento por modelo (usando o
  `LockedUntil` que já está persistido) reduziria a espera.
- **Throttle de webhooks:** o `IndexingChannel` coalesce rajadas pela capacidade 1; um intervalo mínimo
  entre execuções do indexador evitaria que um push contínuo o mantenha em execução permanente.
- **Histórico de falhas global:** a memória da frustração é do daemon (não por tarefa) — o overdrive
  recebe os motivos mais recentes, que podem incluir tarefas anteriores; um `FrustrationTracker` por
  tarefa, com múltiplas retentativas, é a evolução natural.




