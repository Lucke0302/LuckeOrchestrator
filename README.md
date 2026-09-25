# Orquestrador Lucke

Agente autônomo de Engenharia de Software: um **worker service .NET 10** que consome uma **fila no
PostgreSQL**, decide qual modelo de linguagem executa cada tarefa (*Mixture of Experts*), recupera
contexto da **própria base de código** (RAG com `pgvector`), entrega o resultado como
**branch/commit/pull request** no GitHub e devolve o ciclo de vida da tarefa ao banco — sem
intervenção manual no banco e sem clonar o repositório na máquina. A entrega fica **aguardando revisão**:
aprovar (merge) ou rejeitar (fechar o PR e devolver a tarefa à fila com o motivo) acontece pela API do
próprio daemon, que também entrega o log em tempo real ao painel web pelo SignalR — sem DBeaver e sem
`journalctl`.

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
- [Sumarização do Pull Request](#sumarização-do-pull-request)
- [Fila e ciclo de vida da tarefa](#fila-e-ciclo-de-vida-da-tarefa)
- [Host HTTP e webhook do GitHub](#host-http-e-webhook-do-github)
- [Backend em tempo real: API de review e SignalR](#backend-em-tempo-real-api-de-review-e-signalr)
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

O host também expõe o **webhook do GitHub** (que dispara a indexação do RAG sob demanda), a **API de
gerenciamento/revisão** das tarefas e o **hub de logs** consumido pelo painel web (ver
[Backend em tempo real: API de review e SignalR](#backend-em-tempo-real-api-de-review-e-signalr)).
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
   fixo de artefato) e pull request com título e corpo sumarizados por um expert rápido
   (`TaskComplexity.Baixo`) sob o contrato JSON `{"titulo": ..., "descricao": ...}`.
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
| **Application** | Contratos e orquestração de caso de uso | `IAgentTaskRepository`, `ILLMProvider`, `IEmbeddingProvider`, `ICodeContextRepository`, `IGitHubService`, `ITaskRouter`, `IQuotaManager`, `CodebaseIndexerService`, `TaskReviewService`, `PullRequestSummary`, `AgentTaskResponse` |
| **Infrastructure** | Implementações reais | `AppDbContext` (+ migrations), `AgentTaskRepository`, `CodeContextRepository`, `GoogleAiStudioAdapter`, `GitHubAdapter`, `MoETaskRouter`, `DbQuotaManager`, `ModelCatalog`, `AiStudioResponseReader`, `LlmPayloadSanitizer` |
| **Worker** | Composição e laço do daemon | `Program` (host HTTP: webhook + API + hub), `DependencyInjectionSetup`, `LuckeOrchestratorWorker`, `TaskEndpoints`, `OrchestratorWorkerOptions`, `IndexingChannel`, `IndexingBackgroundService`, `SignalRLogSink`, `SignalRLoggerProvider`, `LogHub`, `LogBroadcastService` |
| **Tests** | Testes automatizados (xUnit + Moq + FluentAssertions) | `MoETaskRouterTests`, `FrustrationTrackerTests`, `CodebaseIndexerServiceTests`, `OrchestratorCompositionTests`, `GoogleAiStudioAdapterEmbeddingTests`, `GoogleAiStudioAdapterParseTests`, `LuckeOrchestratorWorkerDeliveryTests`, `TaskReviewServiceTests`, `SignalRLogStreamingTests` |

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
- `FrustrationTracker` (Domain) contabiliza as falhas do daemon — de geração **e** de entrega no
  GitHub — e marca `OverdriveDisparado` ao atingir `Frustration:MaxFailures` — é o gatilho do
  overdrive descrito abaixo.
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

Falhas de **entrega** (criar branch, commit de blob/tree, abertura do PR) entram na mesma memória: o
`GitHubAdapter` loga o erro (`LogError`, com o passo que falhou) e **propaga** a exceção — o worker
registra o motivo no `FrustrationTracker`, marca a tarefa como `Falhou` e segue o laço. A entrega não é
repetida no mesmo ciclo (os artefatos já foram gerados e o problema é do repositório, não do modelo), e
**nenhuma exceção do Octokit é engolida**: branch criada sem commit deixa de ser um "sucesso" silencioso.

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
| Commit | `GET git/ref/heads/{branch}` (referência → SHA) → `GET git/commits/{sha}` (commit + árvore base) → um blob por arquivo → `git/trees` com `BaseTree` (preserva o conteúdo já versionado) → `git/commits` → `PATCH git/refs` |
| Pull request | `POST pulls` (base = `GitHub:BaseBranch`), título e corpo do resumo JSON `{"titulo", "descricao"}` |
| Indexação do RAG | `git/trees?recursive=1` + `git/blobs`, filtrando apenas blobs `.cs` com conteúdo textual |

O Octokit 14 não expõe overloads com `CancellationToken` nas rotas de Git; por isso o adapter valida
o token antes da árvore e a cada blob baixado. Blobs acima do limite da API voltam sem base64 e são
ignorados na indexação. O token do agente autônomo (`GitHub:AgentToken`) é obrigatório: o adapter falha
rápido na construção quando ele não está configurado. As operações de revisão (merge/fechamento de PR)
usam o `GitHub:AdminToken` — ver [Dual-token](#dual-token-o-agente-entrega-o-revisor-aprova).
rápido na construção quando ele não está configurado.

A referência da branch é lida com o prefixo `heads/` (`GET git/ref/heads/feat/task-{id}`) e o commit
base é buscado pelo **SHA** devolvido por ela: a rota `git/commits/{sha}` não aceita nome de
referência no path (responderia `404`), e a árvore nova ancora o `BaseTree` no `Tree.Sha` desse commit.
Como o GitHub replica referências de forma assíncrona, essa leitura repete em até 5 tentativas com 1s
de intervalo (com `LogWarning` por tentativa) antes de propagar o `404` — a branch recém-criada pode
ainda não estar visível nos primeiros instantes.

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

## Sumarização do Pull Request

Depois de gerar os arquivos, o worker resolve um expert rápido (`TaskComplexity.Baixo`) e pede o título
e o corpo do PR:

> "Descreva a mudança implementada para o título e a descrição de um Pull Request que implementou esta
> tarefa: {payload}. Arquivos gerados: ### {caminho} {conteúdo} … Responda apenas com o objeto JSON
> `{"titulo": "...", "descricao": "..."}`, sem cercas de markdown"

O conteúdo de cada arquivo entra limitado a 2000 caracteres (o resumo descreve a mudança, não o código
inteiro, e um prompt gigante só consumiria contexto e cota). O contrato é **JSON estrito**
`{"titulo": "...", "descricao": "..."}`, fixado na instrução do adaptador
(`PullRequestSummaryInstruction`) e repetido no fim do prompt do worker. O parse desserializa o record
`PullRequestSummary` (Application) e alimenta o título e o corpo (*body*) do pull request: título vazio
(ou só com marcações de markdown) cai em `feat(task-{id})`, normalizado para uma linha de até 72
caracteres.

O parse reaproveita a **mesma sanitização dos artefatos** (`SanitizeJsonPayload`, exposta ao host por
`LlmPayloadSanitizer`): os modelos devolvem o objeto cercado por crases de markdown e, sem essa limpeza,
um JSON correto cairia no fallback. Se o sumarizador falhar (cota, timeout), devolver vazio ou devolver
algo fora do contrato (texto livre, JSON sem `descricao`), o PR usa o título e o corpo determinísticos
(`Entrega automática da tarefa …` + payload) e o log recebe um `LogWarning` com o motivo do parse e a
prévia da resposta bruta: a entrega já commitada nunca é desfeita por causa do resumo.

## Auditoria do parse e da entrega (fim do silêncio)

- **Sanitização do JSON:** antes de desserializar, o payload do LLM passa por
  `AiStudioResponseReader.SanitizeJsonPayload`, que remove cercas de markdown (` ```json ... ``` `,
  inclusive coladas no objeto e com prosa dentro do bloco) e o rótulo `json`. A limpeza só roda quando
  o texto **não** é JSON válido, então crases legítimas dentro de um valor de string chegam intactas ao
  commit.
- **Log do LLM em uma linha:** a resposta é registrada com o tamanho total e uma prévia de **200
  caracteres** sem quebras de linha — o corpo multilinha gigante vira `[blob data]` no journal do Linux
  e desaparecia do log.
- **Auditoria do parse:** o número de arquivos extraídos (e quantos são aproveitáveis) é logado antes
  de qualquer efeito no GitHub; JSON válido porém sem nenhum arquivo/caminho utilizável lança
  `InvalidOperationException` — a tarefa cai no tratamento de falha (frustração → overdrive → `Falhou`)
  em vez de seguir para um commit vazio.
- **Commit sem arquivos é erro:** `CommitChangesAsync` recusa um commit vazio de forma explícita (seria
  a branch idêntica à base e o PR vazio).
- **Commit base resolvido pelo SHA:** `CommitChangesAsync` lê `GET git/ref/heads/{branch}` e só então
  `GET git/commits/{sha}` — a rota de commit da Git Data API não aceita nome de referência (respondia
  `404` logo após a criação da branch) — e a árvore nova ancora o `BaseTree` no `Tree.Sha` do commit
  base; sem árvore devolvida, o método lança `InvalidOperationException` em vez de partir do vazio.
- **Consistência eventual do GitHub:** a leitura da referência/commit base repete em até 5 tentativas
  com 1s de intervalo, logando `LogWarning` por tentativa, antes de propagar o `404`.
- **Referência atualizada com force:** o `PATCH git/refs` de `CommitChangesAsync` envia
  `ReferenceUpdate(newCommit.Sha, force: true)`. Em retry/overdrive a branch `feat/task-{id}` pode já
  ter commit de uma tentativa anterior e o commit novo nasce de outro pai — sem o force o GitHub
  responde `422 Update is not a fast forward` e derruba a entrega. Como a branch é temporária,
  exclusiva da tarefa e gerida só pelo agente, o ponteiro é simplesmente reposicionado no commit novo
  (o PR passa a refletir o último ciclo).
- **Trilha do Octokit:** criação de branch, commit (blobs, árvore, referência) e abertura do PR logam
  `LogInformation` com os SHAs/caminhos de cada etapa e `LogError` + rethrow em qualquer falha — o log
  mostra exatamente onde o fluxo parou.
- **Fallback do resumo do PR com evidência:** quando o resumo do pull request não é o JSON do contrato
  (exceção de parse ou ausência de `descricao`), o worker loga `LogWarning` com o motivo **e os
  primeiros 500 caracteres da resposta bruta do LLM** (achatada em uma linha, porque o journal descarta
  corpos multilinha) antes de usar o título/corpo padrão — sem isso um fallback em produção não diz o
  que o modelo devolveu fora do contrato. O resumo **não** alimenta a frustração: a entrega já foi
  commitada e a tarefa segue `Concluida`.

## Fila e ciclo de vida da tarefa

| Status | Significado |
| --- | --- |
| `Pendente` | na fila, aguardando dequeue |
| `EmExecucao` | reivindicada pelo worker (o dequeue já grava esse status) |
| `Concluida` | arquivos commitados e PR aberto; `Branch` e `PullRequestUrl` preenchidos |
| `Falhou` | geração sem arquivos, resposta fora do JSON estrito, falha não recuperável (após a tentativa do overdrive, quando disparado) ou falha de entrega no GitHub (branch/commit/PR) |
| `Cancelada` | interrompida por solicitação/desligamento |
| `Aprovada` | revisão humana aprovou a entrega: o pull request foi mesclado com o `AdminToken` (a conta do agente não aprova o próprio PR) |

O índice `ix_agent_tasks_status_criado_em` (`status`, `criado_em`) atende exatamente o dequeue:
`WHERE status = 'Pendente' ORDER BY criado_em`. A tabela é a única interface com o resto do sistema —
qualquer produtor pode inserir uma linha `Pendente` (com `payload` e `complexidade`) e o daemon a
processa sem reinício — pela API (`POST /api/tasks`), que é o produtor natural do painel web, ou por um
`INSERT` direto no banco. O status é gravado como texto (`varchar(16)`), então o valor novo `Aprovada`
entrou **sem migration**: nenhuma coluna mudou, apenas o domínio passou a conhecer o estado de revisão.

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

## Backend em tempo real: API de review e SignalR

O host deixou de ser apenas o webhook do GitHub: ele é o back-end em tempo real do painel web — as
tarefas são consultadas e enfileiradas por HTTP, a revisão (aprovar/rejeitar) fecha o ciclo do pull
request e o log do daemon chega ao navegador pelo SignalR, sem DBeaver e sem `journalctl`.

### API de gerenciamento (Minimal APIs)

| Rota | Efeito | Token |
| --- | --- | --- |
| `GET /api/tasks?limit=50` | lista as tarefas, da mais recente para a mais antiga (teto de 200 linhas) | — |
| `POST /api/tasks` | recebe `{ "payload": "...", "complexidade": "Medio" }`, gera o UUID, insere `Pendente` e responde **200** com a tarefa | — |
| `POST /api/tasks/{id}/accept` | mescla o pull request da tarefa e marca `Aprovada` | `AdminToken` |
| `POST /api/tasks/{id}/reject` | recebe `{ "motivo": "..." }`, fecha o PR (motivo como comentário), alimenta o medidor de frustração e devolve a tarefa para `Pendente` | `AdminToken` |

- **UUID e status nascem no domínio:** `POST /api/tasks` não inventa identificador nem status — o
  `AgentTask` já chega com `Guid.NewGuid()` e `Pendente`, e o laço reivindica a linha no próximo ciclo
  (sem reinício do host).
- **`complexidade` é opcional** (`Baixo` por padrão) e o JSON da API aceita o enum como texto
  (`"Medio"`, `"Aprovada"`): `ConfigureHttpJsonOptions` registra o `JsonStringEnumConverter` para o
  binding e para a resposta.
- **`accept`:** o merge é pedido pela branch da tarefa (`feat/task-{id}`) com o `AdminToken` e
  `merged = false` (conflito, branch protegida, checks pendentes) é tratado como falha — a tarefa **não**
  vira `Aprovada` sem merge. Tarefa inexistente ⇒ `404`; tarefa sem PR (falhou antes da entrega) ⇒ `409`;
  recusa do GitHub ⇒ `502` com a mensagem original no corpo do `ProblemDetails`.
- **`reject`:** fecha o PR (`state = closed`, com o motivo comentado) com o `AdminToken`, registra
  `revisão humana rejeitou a entrega: {motivo}` no `FrustrationTracker` **compartilhado com o laço** — é
  isso que aproxima o daemon do overdrive — e grava a tarefa de volta como `Pendente`. `Branch` e
  `PullRequestUrl` são preservados: dizem qual entrega foi rejeitada, e a branch é reaproveitada na
  reentrega. Rejeitar uma tarefa sem PR é válido (não há o que fechar): a falha entra no medidor e a
  tarefa volta para a fila.
- **Corpo inválido** (`payload` ou `motivo` em branco) ⇒ `400`. O log técnico sai pelo pipeline de
  `ILogger` (console/journald **e** hub), não na resposta HTTP.

### Streaming de logs (SignalR)

```
ILogger (qualquer categoria do host)
   │  SignalRLoggerProvider — Singleton, [ProviderAlias("LogStream")], fila não bloqueante
   ▼
SignalRLogSink — Channel<LogStreamEntry> limitado (DropOldest) + retrovisor circular
   │  LogBroadcastService (BackgroundService) — único que fala com o hub
   ▼
LogHub — rota /hubs/logs —► "log" (um evento) / "history" (retrovisor ao conectar)
```

- **Provider customizado:** o `SignalRLoggerProvider` entra no pipeline de `ILogger` ao lado do
  console/journald (basta registrar `ILoggerProvider` na DI — o `LoggerFactory` do host resolve todos) e
  converte cada evento em um `LogStreamEntry` (instante UTC, nível, categoria, mensagem já formatada e
  `ToString()` da exceção). Nada é substituído: o journal continua recebendo tudo.
- **Quem loga não espera:** a entrega ao provider é uma escrita em canal limitado; o envio ao hub (I/O de
  rede) acontece no `LogBroadcastService`, um `BackgroundService` — o laço do orquestrador nunca fica
  preso por causa de um painel lento ou desconectado.
- **Sem realimentação:** todo envio roda com `SignalRLogSink.SuppressBroadcast()` ligado; se o SignalR
  logar um erro de transporte durante o envio, o provider descarta esse evento — sem essa trava, uma
  falha de envio geraria log que geraria envio, em laço infinito.
- **Contrato do fio:** JSON em `camelCase` com o nível como texto —
  `{ "timestampUtc": ..., "level": "Information", "category": "...", "message": "...", "exception": null }`.
  Ao conectar, o cliente recebe o retrovisor no evento `history` e depois cada evento em `log`.
- **`LogStreaming`:** `Enabled`, `MinimumLevel` (independe do filtro do journald), `QueueCapacity` (o
  evento mais antigo é descartado quando a fila enche), `HistorySize` (retrovisor; `0` desliga) e
  `MaxMessageLength` (mensagem truncada com `...`).

```javascript
const connection = new signalR.HubConnectionBuilder()
    .withUrl('http://localhost:5080/hubs/logs')  // origem precisa estar em Cors:AllowedOrigins
    .withAutomaticReconnect()
    .build();

connection.on('history', (entries) => entries.forEach(render));
connection.on('log', render);

await connection.start();
```

### Dual-token: o agente entrega, o revisor aprova

| Token | Identidade | Operações |
| --- | --- | --- |
| `GitHub:AgentToken` | agente autônomo | branch, commit, abertura de PR e leitura da árvore (RAG) |
| `GitHub:AdminToken` | revisor humano | merge e fechamento de PR (`accept`/`reject`) |

- o `GitHubAdapter` mantém **dois** clientes Octokit (um por token) e escolhe pelo contexto da ação: a
  entrega nunca usa a credencial de revisão e a revisão nunca usa a do agente;
- **fail closed:** sem `AgentToken` o adapter falha na construção (como antes); sem `AdminToken` a
  operação de revisão falha explicitamente (`InvalidOperationException`) em vez de reusar a credencial
  do agente — um fallback silencioso anularia a segregação de funções;
- `GitHub:MergeMethod` define a estratégia da aprovação (`Merge`, `Squash` ou `Rebase`); valor inválido
  cai em `Merge` com aviso no log;
- o `reject` é idempotente: branch sem PR aberto só registra um aviso (nada a fechar) e a tarefa volta
  para a fila do mesmo jeito.

### CORS

`Cors:AllowedOrigins` lista as origens do painel (as portas usuais de desenvolvimento — `localhost` em
`3000`/`4200`/`5173`/`8080` e os equivalentes em `127.0.0.1` — já vêm no `appsettings.json`); a política
`LuckeWebClient` (`AllowAnyHeader` + `AllowAnyMethod` + `AllowCredentials`) é aplicada por
`app.UseCors(...)` antes do webhook, da API e do hub, então o `negotiate` do SignalR também passa por
ela. Lista vazia ⇒ nenhuma origem cruzada liberada (e nada quebra no start).

## Configuração e segredos

`appsettings.json` (nenhum segredo em código):

| Chave | Papel |
| --- | --- |
| `ConnectionStrings:DefaultConnection` | PostgreSQL (fila + índice vetorial) |
| `AiStudio:BaseUrl` / `ApiVersion` / `TimeoutSeconds` / `QuotaLockoutHours` | endpoint e limites do Google AI Studio |
| `AiStudio:ApiKey` | **segredo** (variável de ambiente / user-secrets) |
| `HttpResilience:RetryAttempts` / `BaseDelaySeconds` | política Polly (retry + backoff) |
| `GitHub:AgentToken` | **segredo** do agente autônomo (entrega: branch, commit e PR) |
| `GitHub:AdminToken` | **segredo** do revisor humano (merge e fechamento de PR na API) |
| `GitHub:MergeMethod` | estratégia de merge da aprovação (`Merge`, `Squash` ou `Rebase`) |
| `GitHub:Owner` / `Repository` / `BaseBranch` | repositório de trabalho e branch base |
| `GitHub:WebhookSecret` | **segredo** do HMAC-SHA256 do webhook (variável de ambiente / user-secrets) |
| `Frustration:MaxFailures` | falhas toleradas antes do overdrive (alimenta o histórico entregue ao overdrive) |
| `Orchestrator:PollingIntervalSeconds` | cadência do laço quando a fila está vazia |
| `Orchestrator:QuotaCooldownMinutes` | cooldown do laço quando toda a cadeia MoE está bloqueada |
| `Orchestrator:IndexingIntervalMinutes` | cadência (rede de segurança) do indexador do RAG |
| `ASPNETCORE_URLS` | endereço/porta do host HTTP que atende o webhook, a API e o hub (default do Kestrel quando omitido) |
| `Cors:AllowedOrigins` | origens do painel web liberadas no CORS (API + `negotiate` do hub) |
| `LogStreaming:Enabled` / `MinimumLevel` / `QueueCapacity` / `HistorySize` / `MaxMessageLength` | streaming de logs: liga/desliga, nível publicado, fila, retrovisor e truncamento |

> **Atenção (systemd/Production):** user-secrets e `appsettings.Development.json` **não** são
> carregados fora do ambiente Development. Em produção, `AiStudio:ApiKey`, `GitHub:AgentToken`,
> `GitHub:AdminToken`, `GitHub:WebhookSecret`, `GitHub:Owner` e `GitHub:Repository` precisam vir de
> variáveis de ambiente do unit (`AiStudio__ApiKey`, `GitHub__AgentToken`, `GitHub__AdminToken`,
> `GitHub__WebhookSecret`, `GitHub__Owner`, `GitHub__Repository`), por exemplo via
> `EnvironmentFile=/etc/lucke/lucke.env`.

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
- **Rejeição na revisão**: o `reject` da API fecha o PR, registra o motivo no mesmo `FrustrationTracker`
  do laço (a rejeição conta como falha para o overdrive) e devolve a tarefa para `Pendente` — o expert
  mais robusto recebe o motivo apontado pelo revisor no próximo ciclo.
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
dotnet user-secrets set "GitHub:AgentToken"     "<token do agente>"  --project OrquestradorLucke.Worker
dotnet user-secrets set "GitHub:AdminToken"     "<token do revisor>" --project OrquestradorLucke.Worker
dotnet user-secrets set "GitHub:WebhookSecret"  "<segredo>" --project OrquestradorLucke.Worker

# 2) Banco: aplica as migrations (assume o DEFAULT_CONNECTION já configurado)
$env:ConnectionStrings__DefaultConnection='Host=<host>;Database=lucke_db;Username=<user>;Password=<senha>'
dotnet ef database update --project OrquestradorLucke.Infrastructure

# 3) Daemon (o host sobe o laço do orquestrador, o host HTTP do webhook, a API de review e o hub de logs)
dotnet run --project OrquestradorLucke.Worker

# 4) Enfileira uma tarefa pela API (o UUID e o status Pendente são gerados no domínio)
INSERT INTO agent_tasks (id, payload, complexidade, status, criado_em)
VALUES (gen_random_uuid(), 'Criar endpoint de cálculo de frete', 'Medio', 'Pendente', now());

# 5) Webhook do GitHub assinado (401 sem assinatura válida; 202 quando o gatilho é enfileirado)
$body      = '{"ref":"refs/heads/main"}'
$secret    = '<segredo>'
$mac       = [System.Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($secret))
$signature = 'sha256=' + [Convert]::ToHexString($mac.ComputeHash([Text.Encoding]::UTF8.GetBytes($body))).ToLowerInvariant()
Invoke-WebRequest -Method Post http://localhost:5000/api/webhook/github `
    -Body $body -ContentType 'application/json' -Headers @{ 'X-Hub-Signature-256' = $signature }

# 6) API de gerenciamento/revisão (o mesmo host que atende o webhook)
$base = 'http://localhost:5000'
Invoke-RestMethod -Method Post "$base/api/tasks" -ContentType 'application/json' `
    -Body '{"payload":"Criar endpoint de cálculo de frete","complexidade":"Medio"}'
Invoke-RestMethod -Method Get  "$base/api/tasks?limit=20"

$taskId = '<id devolvido pelo POST>'
Invoke-RestMethod -Method Post "$base/api/tasks/$taskId/accept"      # merge do PR (AdminToken) ⇒ Aprovada
Invoke-RestMethod -Method Post "$base/api/tasks/$taskId/reject" -ContentType 'application/json' `
    -Body '{"motivo":"Faltou validação de entrada"}'                # fecha o PR + volta para Pendente

# 7) Log em tempo real: o painel web conecta no hub (SignalR) e recebe os eventos "history"/"log"
#    ws://localhost:5000/hubs/logs  (o negotiate sai em POST /hubs/logs/negotiate?negotiateVersion=1)
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
  `DbQuotaManager` (Scoped), `IndexingChannel` (Singleton), `FrustrationTracker` (Singleton, o **mesmo**
  em qualquer escopo — é o que liga a rejeição da API ao overdrive do laço) e `TaskReviewService`
  (Scoped), o registro do provider de log do hub e o fail fast da connection string;
- `GoogleAiStudioAdapterParseTests` — o JSON cercado por crases/alvo de markdown é sanitizado antes da
  desserialização (cercas em linha própria, coladas no objeto, rótulo `json` solto e cerca com prosa),
  crases legítimas dentro de um valor de string não são mutadas, o retorno sem arquivo utilizável falha
  com `InvalidOperationException` e a resposta do LLM é logada em uma única linha truncada;
- `LuckeOrchestratorWorkerDeliveryTests` — falha no commit do GitHub não é engolida: a tarefa é gravada
  como `Falhou`, um único `LogError` traz o passo que falhou e o contador da frustração (1/3), nenhum PR
  é aberto e a tarefa não é marcada como `Concluida`.
- `TaskReviewServiceTests` — a API não inventa dados: `POST /api/tasks` persiste `Pendente` com UUID novo;
  `accept` pede o merge da branch da tarefa, grava `Aprovada` e **não** mexe no medidor de frustração;
  falha no merge não grava status nenhum (`Aprovada` nunca sai sem merge); `reject` fecha o PR com o
  motivo, incrementa o contador compartilhado com o histórico (`revisão humana rejeitou a entrega: ...`)
  e devolve a tarefa para `Pendente`; tarefa inexistente ⇒ `NaoEncontrada`, sem efeito colateral;
- `SignalRLogStreamingTests` — o provider publica nível/categoria/mensagem formatada/exceção, filtra pelo
  `MinimumLevel`, trunca a mensagem no teto, mantém o retrovisor circular com os eventos mais recentes,
  respeita `Enabled` e descarta o log emitido durante a publicação (a trava contra realimentação do
  canal).

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
- **API sem autenticação:** as rotas de gerenciamento/review e o hub não exigem credencial — elas
  assumem o perímetro do daemon (bind em `127.0.0.1` ou atrás de um proxy com autenticação). O
  `AdminToken` é exercido pelo processo, não pelo revisor que usa o painel: expor a API publicamente
  exige autenticação/HTTPS antes.
- **Motivo da rejeição não persistido:** o `motivo` fecha o PR (comentário) e entra no histórico de
  frustração, mas não há coluna em `agent_tasks` para ele — um `review_note` com migration permitiria
  listar as rejeições no painel sem abrir o PR.
- **Retrovisor em memória:** o `history` do hub vive no processo (limitado por `LogStreaming:HistorySize`);
  reiniciar o daemon zera a janela, e um histórico persistido (ou um `journalctl` consultado sob demanda)
  cobriria o que aconteceu antes do start.

