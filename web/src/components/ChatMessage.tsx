import { Brain } from 'lucide-react'

/** Quem assina a mensagem — define a cor e o alinhamento da bolha. */
export type ChatRole = 'agente' | 'operador'

export type ChatMessageProps = {
  /** Rótulo exibido no cabeçalho da bolha. */
  author: string
  /**
   * Conteúdo bruto da mensagem. O que estiver dentro de `<think>...</think>` vira o bloco retrátil
   * "Pensamentos do Agente"; o restante é renderizado como mensagem normal.
   */
  content: string
  /** Papel de quem falou (padrão: o agente). */
  role?: ChatRole
  /** Instante já formatado para exibição; sem valor, o cabeçalho mostra só o autor. */
  timestamp?: string
}

/** Fatia do conteúdo: `think` é a trilha de raciocínio do modelo e `text` é a resposta dele. */
type ChatSegment = {
  kind: 'text' | 'think'
  content: string
}

const THINK_TAG_PATTERN = /<think>([\s\S]*?)<\/think>/gi

/**
 * Quebra o conteúdo nas tags `<think>`: o interior vira um segmento `think` (retrátil) e todo o resto
 * vira segmento `text`.
 *
 * O regex usa `[\s\S]` (e não `.`) para casar as quebras de linha do raciocínio e é criado a cada
 * chamada para não carregar o `lastIndex` de uma execução anterior (`/g` é stateful).
 */
function splitThoughts(content: string): ChatSegment[] {
  const segments: ChatSegment[] = []
  let cursor = 0
  let match = THINK_TAG_PATTERN.exec(content)

  while (match !== null) {
    const textBefore = content.slice(cursor, match.index).trim()

    if (textBefore.length > 0) {
      segments.push({ kind: 'text', content: textBefore })
    }

    segments.push({ kind: 'think', content: match[1].trim() })
    cursor = match.index + match[0].length
    match = THINK_TAG_PATTERN.exec(content)
  }

  const remainder = content.slice(cursor).trim()

  if (remainder.length > 0 || segments.length === 0) {
    segments.push({ kind: 'text', content: remainder })
  }

  return segments
}

/**
 * Bolha do chat do agente. O raciocínio do modelo (`<think>`) não é escondido — fica atrás de um
 * `<details>` fechado por padrão, para o operador abrir só quando quiser auditar o caminho da
 * resposta sem quebrar a leitura da conversa.
 */
export function ChatMessage({ author, content, role = 'agente', timestamp }: ChatMessageProps) {
  const isOperator = role === 'operador'
  const segments = splitThoughts(content)

  const bubbleClass = isOperator
    ? 'border-lucke-blue/40 bg-lucke-blue/15 text-white'
    : 'border-lucke-blue-soft/40 bg-lucke-blue-darker/80 text-lucke-blue-lighter'

  return (
    <article className={`flex flex-col gap-1 ${isOperator ? 'items-end' : 'items-start'}`}>
      <header className="flex items-center gap-2 px-1 text-[11px] uppercase tracking-wide text-lucke-blue-lighter/60">
        <span className="font-semibold text-lucke-blue-light">{author}</span>
        {timestamp && <span className="font-mono normal-case">{timestamp}</span>}
      </header>

      <div className={`w-full rounded-xl border px-3 py-2 text-sm ${bubbleClass}`}>
        {/* A chave é o índice porque a lista vem de um texto imutável: não há reordenação possível. */}
        {segments.map((segment, index) =>
          segment.kind === 'think' ? (
            <details
              key={index}
              className="my-1 rounded-lg border border-lucke-blue-soft/40 bg-lucke-blue-darkest/60 px-3 py-2 first:mt-0 last:mb-0"
            >
              <summary className="flex cursor-pointer list-none items-center gap-2 text-xs font-medium text-lucke-blue-light">
                <Brain size={14} aria-hidden="true" />
                Pensamentos do Agente
              </summary>
              <p className="mt-2 whitespace-pre-wrap text-xs leading-relaxed text-lucke-blue-lighter/80">
                {segment.content}
              </p>
            </details>
          ) : (
            <p key={index} className="whitespace-pre-wrap leading-relaxed">
              {segment.content}
            </p>
          ),
        )}
      </div>
    </article>
  )
}

export default ChatMessage
