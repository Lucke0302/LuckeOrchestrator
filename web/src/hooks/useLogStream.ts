import { useEffect, useState } from 'react'
import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import { ACCESS_TOKEN_STORAGE_KEY, API_BASE_URL } from '../services/api'

/**
 * Contrato do fio do hub de logs — os nomes vêm de `LogStreamContract` (Worker/.NET) e renomear um
 * deles lá sem ajustar aqui quebraria o terminal silenciosamente.
 */
const LOG_EVENT_NAME = 'log'
const HISTORY_EVENT_NAME = 'history'

/** Rota do hub de logs no host do daemon. */
const LOG_HUB_PATH = '/hubs/logs'

/** Sufixo da URL da API que o host troca pelo caminho do hub. */
const API_PATH_SUFFIX = '/api'

/** Teto de linhas mantidas em estado (e no DOM) — o retrovisor do daemon é bem maior que isso. */
export const MAX_LOG_ENTRIES = 100

/** Nível de log como texto, igual ao que o `ILogger` serializa no evento. */
export type LogLevelName =
  | 'Trace'
  | 'Debug'
  | 'Information'
  | 'Warning'
  | 'Error'
  | 'Critical'
  | 'None'

/** Um evento do hub: espelha o record `LogStreamEntry` (camelCase, nível como texto). */
export type LogStreamEntry = {
  timestampUtc: string
  level: LogLevelName
  category: string
  message: string
  exception: string | null
}

/**
 * URL do hub de logs: `VITE_HUB_URL` quando o hub estiver atrás de outro endereço; caso contrário,
 * derivada da URL da API.
 *
 * A troca do sufixo `/api` é feita pelo **fim** da string de propósito: um `replace('/api', ...)`
 * simples casaria o primeiro `api` do endereço e corromperia hosts que já o trazem no nome (ex.:
 * `https://api.lucke.com/api` viraria `https://hubs/logs.lucke.com/api`).
 */
export function resolveLogHubUrl(): string {
  const explicitHubUrl = import.meta.env.VITE_HUB_URL?.trim()

  if (explicitHubUrl) {
    return explicitHubUrl
  }

  const baseUrl = API_BASE_URL.replace(/\/+$/, '')

  return baseUrl.endsWith(API_PATH_SUFFIX)
    ? `${baseUrl.slice(0, -API_PATH_SUFFIX.length)}${LOG_HUB_PATH}`
    : `${baseUrl}${LOG_HUB_PATH}`
}

/**
 * O `stop()` do cleanup (React Strict Mode monta/desmonta/remonta em dev) pode abortar a negociação em
 * voo e o SignalR rejeita o `start()` com `AbortError`. Não é falha real, então é reconhecida e
 * silenciada para não poluir o console do terminal.
 */
function isStrictModeTeardownAbort(error: unknown): boolean {
  if (!(error instanceof Error)) {
    return false
  }

  return (
    error.name === 'AbortError' ||
    error.message.includes('stopped during negotiation') ||
    error.message.includes('Failed to start the HttpConnection before stop() was called')
  )
}

/**
 * Assina o hub de logs (`/hubs/logs`) e devolve o stream em estado, já com o retrovisor que o daemon
 * envia ao conectar (evento `history`).
 *
 * A conexão é criada no mount e derrubada no unmount: o array fica limitado a `MAX_LOG_ENTRIES`
 * linhas para o DOM não crescer indefinidamente com um daemon falante. O token vai pela
 * `accessTokenFactory` — o hub é `[Authorize]` e o WebSocket não envia o cabeçalho `Authorization`,
 * então o SignalR o coloca na query string do negotiate.
 */
export function useLogStream(): LogStreamEntry[] {
  const [entries, setEntries] = useState<LogStreamEntry[]>([])

  useEffect(() => {
    // O cleanup do Strict Mode (dev) roda antes de o `start()` concluir: o sinal de desmontagem
    // permite engolir o abort da negociação sem alarme falso no console.
    let isDisposed = false

    const connection = new HubConnectionBuilder()
      .withUrl(resolveLogHubUrl(), {
        accessTokenFactory: () => localStorage.getItem(ACCESS_TOKEN_STORAGE_KEY) ?? '',
      })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build()

    const appendEntries = (incoming: LogStreamEntry[]) => {
      if (incoming.length === 0) {
        return
      }

      // Descarta as linhas mais antigas: o terminal é um painel de acompanhamento, não um arquivo.
      setEntries((current) => [...current, ...incoming].slice(-MAX_LOG_ENTRIES))
    }

    const handleLog = (entry: LogStreamEntry) => appendEntries([entry])
    const handleHistory = (history: LogStreamEntry[]) => appendEntries(history)

    connection.on(LOG_EVENT_NAME, handleLog)
    connection.on(HISTORY_EVENT_NAME, handleHistory)

    void connection.start().catch((error: unknown) => {
      // Sem retry próprio: o `withAutomaticReconnect` cobre a queda de uma conexão já estabelecida e o
      // painel reconecta ao remontar (recarregar a página ou voltar para o dashboard).
      //
      // Com o componente já desmontado (Strict Mode monta → desmonta → remonta), o `stop()` do cleanup
      // aborta a negociação em voo e o `start()` rejeita: é ruído de ciclo de vida, engolido por
      // completo — sem `console.error`/`console.warn` para não assustar no terminal.
      if (isDisposed || isStrictModeTeardownAbort(error)) {
        return
      }

      console.warn('[lucke] Falha ao conectar no hub de logs.', error)
    })

    return () => {
      isDisposed = true

      connection.off(LOG_EVENT_NAME, handleLog)
      connection.off(HISTORY_EVENT_NAME, handleHistory)

      if (connection.state !== HubConnectionState.Disconnected) {
        void connection.stop().catch(() => undefined)
      }
    }
  }, [])

  return entries
}
