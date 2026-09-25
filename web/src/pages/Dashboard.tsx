import { useEffect, useRef, useState } from 'react'
import axios from 'axios'
import {
  AlertCircle,
  Bot,
  Check,
  ExternalLink,
  GitPullRequest,
  Loader2,
  LogOut,
  RefreshCw,
  Terminal,
  X,
} from 'lucide-react'
import { ChatMessage } from '../components/ChatMessage'
import type { ChatRole } from '../components/ChatMessage'
import { Skeleton } from '../components/ui/Skeleton'
import { useAuth } from '../contexts/AuthContext'
import { MAX_LOG_ENTRIES, useLogStream } from '../hooks/useLogStream'
import { acceptTask, getTasks, rejectTask } from '../services/TaskService'
import type { AgentTaskStatus, Task, TaskComplexity } from '../services/TaskService'

/** ProblemDetails devolvido pela Minimal API (título/detalhe das recusas e das falhas do GitHub). */
type ProblemDetails = {
  title?: string
  detail?: string
}

/** Rótulo e classe Tailwind de um badge (status da tarefa e complexidade do roteador MoE). */
type BadgePresentation = {
  label: string
  className: string
}

/** Badge neutro: usado quando a API publica um valor que os mapas locais ainda não conhecem. */
const FALLBACK_BADGE_CLASSNAME = 'border-slate-400/30 bg-slate-400/10 text-slate-300'

/**
 * Normaliza a chave do enum para o PascalCase do Domain: a Minimal API pode publicar o valor em
 * camelCase (`emExecucao`), então a busca crua falharia e devolveria `undefined`.
 *
 * O valor vem do fio e pode não ser string (`null`, número, ausente): sem a guarda o `charAt` estoura
 * com `TypeError` e derruba o card — a string vazia apenas deixa a busca cair no fallback.
 */
function normalizeEnumKey(value: unknown): string {
  if (!value || typeof value !== 'string') {
    return ''
  }

  return value.charAt(0).toUpperCase() + value.slice(1)
}

/**
 * Resolve a apresentação de um badge sem nunca devolver `undefined` — ler `.className` de `undefined`
 * derruba a renderização do dashboard inteira. Tenta a chave crua, a normalizada e, por fim, o neutro.
 */
function resolveBadge<K extends string, T>(
  presentation: Partial<Record<K, T>>,
  value: string,
  fallback: T,
): T {
  const map = presentation as Partial<Record<string, T>>

  return map[value] ?? map[normalizeEnumKey(value) as K] ?? fallback
}

/** Rótulo e cores de cada status da tarefa (as chaves são os valores do enum do Domain). */
const STATUS_PRESENTATION: Record<AgentTaskStatus, BadgePresentation> = {
  Pendente: { label: 'Pendente', className: 'border-amber-400/30 bg-amber-400/10 text-amber-300' },
  EmExecucao: { label: 'Em execução', className: 'border-sky-400/30 bg-sky-400/10 text-sky-300' },
  Concluida: {
    label: 'Aguardando revisão',
    className: 'border-lucke-blue-light/40 bg-lucke-blue/15 text-lucke-blue-light',
  },
  Aprovada: {
    label: 'Aprovada',
    className: 'border-emerald-400/30 bg-emerald-400/10 text-emerald-300',
  },
  Falhou: { label: 'Falhou', className: 'border-rose-400/30 bg-rose-400/10 text-rose-300' },
  Cancelada: { label: 'Cancelada', className: 'border-slate-400/30 bg-slate-400/10 text-slate-300' },
}

/** Rótulo e cores de cada complexidade (é o que o roteador MoE usa para escolher o expert). */
const COMPLEXITY_PRESENTATION: Record<TaskComplexity, BadgePresentation> = {
  Baixo: { label: 'Baixo', className: 'border-slate-400/30 bg-slate-400/10 text-slate-300' },
  Medio: { label: 'Médio', className: 'border-sky-400/30 bg-sky-400/10 text-sky-300' },
  Alto: { label: 'Alto', className: 'border-violet-400/30 bg-violet-400/10 text-violet-300' },
  Critico: { label: 'Crítico', className: 'border-rose-400/30 bg-rose-400/10 text-rose-300' },
}

/** Cor de cada nível de log — o daemon serializa o nível como texto (`Warning`, `Error`, ...). */
const LOG_LEVEL_CLASSES: Record<string, string> = {
  Trace: 'text-lucke-blue-lighter/50',
  Debug: 'text-lucke-blue-lighter/70',
  Information: 'text-sky-300',
  Warning: 'text-amber-300',
  Error: 'text-rose-400',
  Critical: 'font-semibold text-rose-400',
  None: 'text-lucke-blue-lighter/50',
}

const taskDateFormatter = new Intl.DateTimeFormat('pt-BR', {
  day: '2-digit',
  month: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
})

const clockFormatter = new Intl.DateTimeFormat('pt-BR', {
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
})

/** Formata o instante da tarefa; valor inválido volta cru em vez de virar "Invalid Date". */
function formatTaskDate(value: string): string {
  const date = new Date(value)

  return Number.isNaN(date.getTime()) ? value : taskDateFormatter.format(date)
}

/** Hora do evento de log (o UTC do daemon é exibido no fuso do navegador do operador). */
function formatLogTime(value: string): string {
  const date = new Date(value)

  return Number.isNaN(date.getTime()) ? value : clockFormatter.format(date)
}

/** Traduz a falha do Axios para uma linha do painel (ProblemDetails do daemon ou rede fora). */
function resolveApiError(error: unknown, fallback: string): string {
  if (!axios.isAxiosError(error)) {
    return fallback
  }

  if (!error.response) {
    return 'Falha de rede: não foi possível alcançar o Orquestrador Lucke.'
  }

  const { status, data } = error.response

  if (status === 401) {
    return 'Sessão expirada: entre novamente para continuar.'
  }

  return (data as ProblemDetails | null)?.detail ?? `O servidor respondeu ${status}. Tente novamente.`
}

/**
 * A tarefa está na mão do operador: `Concluida` é o pull request aguardando revisão e `Falhou` ainda
 * pode ser devolvida à fila com um motivo (é o texto que alimenta o overdrive do agente).
 */
function isReviewable(status: AgentTaskStatus): boolean {
  return status === 'Concluida' || status === 'Falhou'
}

/** Item da thread do chat (o envio de mensagens depende do endpoint de chat do agente). */
type ChatThreadItem = {
  id: string
  author: string
  role: ChatRole
  timestamp: string
  content: string
}

/**
 * Mensagem fixa de abertura, até o daemon publicar o endpoint de chat do agente. Serve de prova viva
 * do componente `ChatMessage`: o bloco de raciocínio fica atrás do `<details>`.
 */
function createWelcomeThread(): ChatThreadItem[] {
  return [
    {
      id: 'boas-vindas',
      author: 'Orquestrador',
      role: 'agente',
      timestamp: clockFormatter.format(new Date()),
      content: [
        'Chat do agente em preparação: as mensagens do operador e as respostas do modelo aparecem aqui assim que o daemon expuser o endpoint de chat.',
        '<think>O raciocínio do modelo vem embrulhado nas tags de pensamento e o painel o guarda em um bloco retrátil — assim o operador audita o caminho da resposta sem perder a leitura da conversa.</think>',
        'Enquanto isso, o terminal de logs ao lado é a fonte em tempo real do que o daemon está fazendo.',
      ].join('\n\n'),
    },
  ]
}

/** Desfecho de uma revisão aplicada, mostrado no topo da coluna de tarefas. */
type TaskFeedback = {
  kind: 'success' | 'error'
  message: string
}

/** Dashboard principal: tarefas, chat do agente e logs em tempo real, lado a lado. */
function Dashboard() {
  const { logout } = useAuth()
  const logs = useLogStream()
  const [tasks, setTasks] = useState<Task[]>([])
  const [isLoadingTasks, setIsLoadingTasks] = useState(true)
  const [listError, setListError] = useState<string | null>(null)
  const [feedback, setFeedback] = useState<TaskFeedback | null>(null)
  // Tarefa com uma operação em voo: trava os botões daquele card (e não a lista inteira).
  const [busyTaskId, setBusyTaskId] = useState<string | null>(null)
  // Tarefa com o formulário de rejeição aberto e o motivo digitado nele.
  const [rejectingTaskId, setRejectingTaskId] = useState<string | null>(null)
  const [rejectReason, setRejectReason] = useState('')
  const [thread] = useState<ChatThreadItem[]>(createWelcomeThread)
  const terminalRef = useRef<HTMLDivElement | null>(null)

  // Cada carga da fila é um pedido identificado: `0` é a carga inicial e cada clique em Atualizar
  // abre um pedido novo (é o que re-dispara o efeito).
  const [loadRequest, setLoadRequest] = useState(0)

  useEffect(() => {
    let isActive = true

    // A carga é uma função *declarada dentro do efeito* de propósito: toda atualização de estado só
    // acontece depois do `await`, fora do caminho síncrono do efeito — um `setState` síncrono aqui
    // dispara renders em cascata (regra react-hooks/set-state-in-effect).
    async function loadTasks() {
      const isFirstLoad = loadRequest === 0

      try {
        const data = await getTasks()

        if (!isActive) {
          return
        }

        setTasks(data)
        setListError(null)
      } catch (error) {
        if (!isActive) {
          return
        }

        setListError(
          resolveApiError(
            error,
            isFirstLoad
              ? 'Não foi possível carregar as tarefas do Orquestrador.'
              : 'A recarga da fila falhou: verifique a conexão com o daemon.',
          ),
        )
      } finally {
        if (isActive) {
          setIsLoadingTasks(false)
        }
      }
    }

    void loadTasks()

    return () => {
      isActive = false
    }
  }, [loadRequest])

  // O terminal acompanha o fim do stream: cada evento novo rola o bloco para a última linha.
  useEffect(() => {
    const terminal = terminalRef.current

    if (terminal) {
      terminal.scrollTop = terminal.scrollHeight
    }
  }, [logs])

  /**
   * Aplica uma revisão (aceitar/rejeitar), troca o card pela tarefa que a API devolveu e libera a UI.
   * A resposta da API já é o estado final da tarefa, então a lista não precisa ser recarregada.
   */
  async function runReview(taskId: string, action: () => Promise<Task>, successMessage: string) {
    setBusyTaskId(taskId)
    setFeedback(null)

    try {
      const updated = await action()

      setTasks((current) => current.map((task) => (task.id === updated.id ? updated : task)))
      setFeedback({ kind: 'success', message: successMessage })
      setRejectingTaskId(null)
      setRejectReason('')
    } catch (error) {
      setFeedback({ kind: 'error', message: resolveApiError(error, 'A revisão da tarefa falhou.') })
    } finally {
      setBusyTaskId(null)
    }
  }

  function handleAccept(task: Task) {
    // O merge é irreversível pela interface: a confirmação evita aprovar a entrega errada.
    const confirmed = window.confirm(
      `Aprovar a entrega da tarefa "${task.payload}"? O pull request será mesclado com o token administrativo.`,
    )

    if (!confirmed) {
      return
    }

    void runReview(task.id, () => acceptTask(task.id), 'Tarefa aprovada: pull request mesclado.')
  }

  function handleReject(task: Task) {
    const motivo = rejectReason.trim()

    if (!motivo) {
      setFeedback({
        kind: 'error',
        message: 'Informe o motivo da rejeição: ele fecha o pull request e alimenta o overdrive do agente.',
      })
      return
    }

    void runReview(
      task.id,
      () => rejectTask(task.id, motivo),
      'Tarefa rejeitada: o pull request foi fechado e a tarefa voltou para a fila.',
    )
  }

  function openRejectForm(taskId: string) {
    setRejectingTaskId(taskId)
    setRejectReason('')
    setFeedback(null)
  }

  function cancelReject() {
    setRejectingTaskId(null)
    setRejectReason('')
  }

  function handleRefresh() {
    setIsLoadingTasks(true)
    setFeedback(null)
    // O contador é o gatilho do efeito de carga: incrementá-lo dispara um pedido novo.
    setLoadRequest((request) => request + 1)
  }

  return (
    <div className="flex h-screen flex-col overflow-hidden bg-lucke-blue-darkest text-lucke-blue-lighter">
      <header className="flex shrink-0 items-center justify-between gap-4 border-b border-lucke-blue-soft/40 bg-lucke-blue-darker px-4 py-3 sm:px-6">
        <div className="flex items-center gap-3">
          <span className="rounded-xl bg-lucke-blue/15 p-2 text-lucke-blue-light">
            <Bot size={22} aria-hidden="true" />
          </span>
          <div>
            <h1 className="text-base font-semibold text-white sm:text-lg">Lucke</h1>
            <p className="text-xs text-lucke-blue-lighter/70">
              Revisão de entregas, chat do agente e log do daemon em tempo real
            </p>
          </div>
        </div>

        <button
          type="button"
          onClick={logout}
          className="flex items-center gap-2 rounded-xl border border-lucke-blue-soft/40 px-3 py-2 text-sm font-medium text-lucke-blue-lighter transition hover:border-lucke-blue-light hover:text-white focus:outline-none focus:ring-2 focus:ring-lucke-blue/50"
        >
          <LogOut size={16} aria-hidden="true" />
          Sair
        </button>
      </header>

      <main className="grid min-h-0 flex-1 grid-cols-1 gap-4 overflow-y-auto p-4 lg:grid-cols-[1.05fr_1fr_1.2fr] lg:overflow-hidden">
        {/* Coluna 1 — Tarefas: o que a fila tem e a revisão (aceitar/rejeitar) à mão. */}
        <section
          aria-label="Tarefas"
          className="flex min-h-[320px] flex-col overflow-hidden rounded-xl border border-lucke-blue-soft/40 bg-lucke-blue-dark lg:min-h-0"
        >
          <header className="flex shrink-0 items-center justify-between gap-2 border-b border-lucke-blue-soft/40 px-3 py-2">
            <div className="flex items-center gap-2">
              <GitPullRequest className="text-lucke-blue-light" size={18} aria-hidden="true" />
              <h2 className="text-sm font-semibold text-white">Tarefas</h2>
              <span className="rounded-full bg-lucke-blue/15 px-2 py-0.5 font-mono text-[11px] text-lucke-blue-light">
                {tasks.length}
              </span>
            </div>

            <button
              type="button"
              onClick={handleRefresh}
              disabled={isLoadingTasks}
              title="Recarregar a fila"
              className="rounded-lg p-1.5 text-lucke-blue-lighter/70 transition hover:bg-lucke-blue/15 hover:text-white focus:outline-none focus:ring-2 focus:ring-lucke-blue/50 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {isLoadingTasks ? (
                <Loader2 className="animate-spin" size={16} aria-hidden="true" />
              ) : (
                <RefreshCw size={16} aria-hidden="true" />
              )}
            </button>
          </header>

          <div className="min-h-0 flex-1 space-y-3 overflow-y-auto p-3">
            {feedback && (
              <p
                role="status"
                className={`flex items-start gap-2 rounded-xl border p-3 text-xs ${
                  feedback.kind === 'success'
                    ? 'border-emerald-400/30 bg-emerald-400/10 text-emerald-300'
                    : 'border-rose-400/30 bg-rose-500/10 text-rose-300'
                }`}
              >
                {feedback.kind === 'success' ? (
                  <Check className="mt-0.5 shrink-0" size={15} aria-hidden="true" />
                ) : (
                  <AlertCircle className="mt-0.5 shrink-0" size={15} aria-hidden="true" />
                )}
                <span>{feedback.message}</span>
              </p>
            )}

            {listError && (
              <p
                role="alert"
                className="flex items-start gap-2 rounded-xl border border-rose-400/30 bg-rose-500/10 p-3 text-xs text-rose-300"
              >
                <AlertCircle className="mt-0.5 shrink-0" size={15} aria-hidden="true" />
                <span>{listError}</span>
              </p>
            )}

            {isLoadingTasks && tasks.length === 0 && (
              <div className="space-y-3" role="status" aria-label="Carregando tarefas">
                <Skeleton className="h-24 rounded-xl" />
                <Skeleton className="h-24 rounded-xl" />
                <Skeleton className="h-24 rounded-xl" />
              </div>
            )}

            {!isLoadingTasks && tasks.length === 0 && (
              <p className="rounded-xl border border-dashed border-lucke-blue-soft/40 p-4 text-center text-xs text-lucke-blue-lighter/60">
                Nenhuma tarefa na fila. Enfileire um payload pela API para o daemon reivindicar.
              </p>
            )}

            {tasks.map(renderTaskCard)}
          </div>
        </section>

        {/* Coluna 2 — Chat do agente (base): transporte de mensagens ainda não existe no daemon. */}
        <section
          aria-label="Chat do agente"
          className="flex min-h-[320px] flex-col overflow-hidden rounded-xl border border-lucke-blue-soft/40 bg-lucke-blue-dark lg:min-h-0"
        >
          <header className="flex shrink-0 items-center justify-between gap-2 border-b border-lucke-blue-soft/40 px-3 py-2">
            <div className="flex items-center gap-2">
              <Bot className="text-lucke-blue-light" size={18} aria-hidden="true" />
              <h2 className="text-sm font-semibold text-white">Chat do Agente</h2>
            </div>
            <span className="rounded-full border border-amber-400/30 bg-amber-400/10 px-2 py-0.5 text-[11px] text-amber-300">
              Em preparação
            </span>
          </header>

          <div className="min-h-0 flex-1 space-y-3 overflow-y-auto p-3">
            {thread.map((item) => (
              <ChatMessage
                key={item.id}
                author={item.author}
                content={item.content}
                role={item.role}
                timestamp={item.timestamp}
              />
            ))}
          </div>

          <div className="shrink-0 border-t border-lucke-blue-soft/40 p-3">
            <textarea
              rows={2}
              disabled
              placeholder="Envio de mensagens chega com o endpoint de chat do agente."
              className="w-full resize-none rounded-xl border border-lucke-blue-soft/40 bg-lucke-blue-darker px-3 py-2 text-sm text-white placeholder:text-lucke-blue-lighter/40 disabled:cursor-not-allowed disabled:opacity-60"
            />
          </div>
        </section>

        {/* Coluna 3 — Terminal de logs em tempo real (hub SignalR `/hubs/logs`). */}
        <section
          aria-label="Logs em tempo real"
          className="flex min-h-[320px] flex-col overflow-hidden rounded-xl border border-lucke-blue-soft/40 bg-black lg:min-h-0"
        >
          <header className="flex shrink-0 items-center justify-between gap-2 border-b border-lucke-blue-soft/40 bg-lucke-blue-darker px-3 py-2">
            <div className="flex items-center gap-2">
              <Terminal className="text-lucke-blue-light" size={18} aria-hidden="true" />
              <h2 className="text-sm font-semibold text-white">Logs em tempo real</h2>
            </div>
            <span className="font-mono text-[11px] text-lucke-blue-lighter/60">
              {logs.length}/{MAX_LOG_ENTRIES}
            </span>
          </header>

          <div
            ref={terminalRef}
            className="min-h-0 flex-1 overflow-y-auto bg-black p-3 font-mono text-[11px] leading-relaxed sm:text-xs"
          >
            {logs.length === 0 ? (
              <p className="text-lucke-blue-lighter/40">Aguardando eventos do daemon…</p>
            ) : (
              logs.map((entry, index) => (
                // O evento do hub não traz identificador: o par instante+posição é a chave possível.
                <div
                  key={`${entry.timestampUtc}-${index}`}
                  className="flex gap-2 border-b border-white/5 py-1 last:border-b-0"
                >
                  <span className="shrink-0 text-lucke-blue-lighter/50" title={entry.timestampUtc}>
                    {formatLogTime(entry.timestampUtc)}
                  </span>
                  <span
                    className={`w-20 shrink-0 uppercase ${LOG_LEVEL_CLASSES[entry.level] ?? 'text-lucke-blue-lighter/80'}`}
                  >
                    {entry.level}
                  </span>
                  <span className="min-w-0 flex-1">
                    <span className="block text-lucke-blue-lighter/50">{entry.category}</span>
                    <span className="block whitespace-pre-wrap text-gray-100">{entry.message}</span>
                    {entry.exception && (
                      <span className="mt-1 block whitespace-pre-wrap text-rose-400/80">
                        {entry.exception}
                      </span>
                    )}
                  </span>
                </div>
              ))
            )}
          </div>
        </section>

      </main>
    </div>
  )

  /** Card de uma tarefa da fila: payload, status, complexidade, PR e as ações de revisão. */
  function renderTaskCard(task: Task) {
    // A API pode publicar o enum em camelCase: sem o fallback um par não reconhecido derruba o card.
    const status = resolveBadge(STATUS_PRESENTATION, task.status, {
      label: task.status,
      className: FALLBACK_BADGE_CLASSNAME,
    })
    const complexity = resolveBadge(COMPLEXITY_PRESENTATION, task.complexidade, {
      label: task.complexidade,
      className: FALLBACK_BADGE_CLASSNAME,
    })

    return (
      <article
        key={task.id}
        className="rounded-xl border border-lucke-blue-soft/40 bg-lucke-blue-darker/70 p-3"
      >
        <div className="flex items-start justify-between gap-3">
          <p className="min-w-0 flex-1 text-sm font-medium text-white">{task.payload}</p>
          <span
            className={`shrink-0 rounded-full border px-2 py-0.5 text-[11px] font-medium ${status.className}`}
          >
            {status.label}
          </span>
        </div>

        <div className="mt-2 flex flex-wrap items-center gap-2 text-[11px]">
          <span className={`rounded-full border px-2 py-0.5 font-medium ${complexity.className}`}>
            {complexity.label}
          </span>
          <span className="font-mono text-lucke-blue-lighter/60" title={task.criadoEm}>
            {formatTaskDate(task.criadoEm)}
          </span>
          <span className="font-mono text-lucke-blue-lighter/40" title={task.id}>
            #{task.id.slice(0, 8)}
          </span>
        </div>

        {task.pullRequestUrl ? (
          <a
            href={task.pullRequestUrl}
            target="_blank"
            rel="noreferrer"
            className="mt-2 inline-flex items-center gap-1 text-xs text-lucke-blue-light transition hover:text-white"
          >
            <ExternalLink size={13} aria-hidden="true" />
            Abrir pull request
          </a>
        ) : (
          <p className="mt-2 text-xs text-lucke-blue-lighter/40">
            Sem pull request{task.branch ? ` (branch ${task.branch})` : ''}
          </p>
        )}

        {isReviewable(task.status) && renderReviewArea(task)}
      </article>
    )
  }

  /**
   * Ações de revisão do card: `Concluida` (PR aguardando revisão) e `Falhou` são revisáveis — nos
   * demais estados a tarefa é do daemon. A rejeição abre o motivo no próprio card, porque é o texto
   * que vai como comentário no PR e alimenta o medidor de frustração.
   */
  function renderReviewArea(task: Task) {
    const isBusy = busyTaskId === task.id

    if (rejectingTaskId === task.id) {
      return renderRejectForm(task, isBusy)
    }

    return (
      <div className="mt-3 flex flex-wrap gap-2">
        <button
          type="button"
          onClick={() => handleAccept(task)}
          disabled={isBusy || !task.pullRequestUrl}
          title={
            task.pullRequestUrl
              ? 'Mescla o pull request e marca a tarefa como aprovada'
              : 'A tarefa não tem pull request aberto para mesclar'
          }
          className="flex items-center gap-1.5 rounded-lg bg-lucke-blue px-3 py-1.5 text-xs font-semibold text-white transition hover:bg-lucke-blue-light focus:outline-none focus:ring-2 focus:ring-lucke-blue/50 disabled:cursor-not-allowed disabled:opacity-50"
        >
          {isBusy ? (
            <Loader2 className="animate-spin" size={14} aria-hidden="true" />
          ) : (
            <Check size={14} aria-hidden="true" />
          )}
          Aceitar
        </button>

        <button
          type="button"
          onClick={() => openRejectForm(task.id)}
          disabled={isBusy}
          className="flex items-center gap-1.5 rounded-lg border border-rose-400/40 px-3 py-1.5 text-xs font-medium text-rose-300 transition hover:bg-rose-500/10 focus:outline-none focus:ring-2 focus:ring-rose-400/40 disabled:cursor-not-allowed disabled:opacity-50"
        >
          <X size={14} aria-hidden="true" />
          Rejeitar
        </button>
      </div>
    )
  }

  /** Formulário de rejeição do card: o motivo é obrigatório (sem ele o daemon responde `400`). */
  function renderRejectForm(task: Task, isBusy: boolean) {
    return (
      <div className="mt-3 space-y-2">
        <label className="sr-only" htmlFor={`motivo-${task.id}`}>
          Motivo da rejeição
        </label>
        <textarea
          id={`motivo-${task.id}`}
          rows={3}
          autoFocus
          value={rejectReason}
          onChange={(event) => setRejectReason(event.target.value)}
          placeholder="Motivo da rejeição (vai como comentário no PR e alimenta o overdrive do agente)"
          className="w-full resize-none rounded-lg border border-lucke-blue-soft/40 bg-lucke-blue-darkest px-2.5 py-2 text-xs text-white placeholder:text-lucke-blue-lighter/40 focus:border-lucke-blue-light focus:outline-none focus:ring-2 focus:ring-lucke-blue/40"
        />
        <div className="flex flex-wrap gap-2">
          <button
            type="button"
            onClick={() => handleReject(task)}
            disabled={isBusy}
            className="flex items-center gap-1.5 rounded-lg bg-rose-500/90 px-3 py-1.5 text-xs font-semibold text-white transition hover:bg-rose-500 focus:outline-none focus:ring-2 focus:ring-rose-400/50 disabled:cursor-not-allowed disabled:opacity-60"
          >
            {isBusy ? (
              <Loader2 className="animate-spin" size={14} aria-hidden="true" />
            ) : (
              <X size={14} aria-hidden="true" />
            )}
            Confirmar rejeição
          </button>
          <button
            type="button"
            onClick={cancelReject}
            disabled={isBusy}
            className="rounded-lg border border-lucke-blue-soft/40 px-3 py-1.5 text-xs font-medium text-lucke-blue-lighter transition hover:text-white disabled:cursor-not-allowed disabled:opacity-60"
          >
            Cancelar
          </button>
        </div>
      </div>
    )
  }



}

export default Dashboard

