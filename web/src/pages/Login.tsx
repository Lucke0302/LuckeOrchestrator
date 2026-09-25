import { useState } from 'react'
import type { FormEvent } from 'react'
import axios from 'axios'
import { AlertCircle, Bot, Loader2, Lock, LogIn, User } from 'lucide-react'
import { Navigate } from 'react-router-dom'
import { useAuth } from '../contexts/AuthContext'

/** ProblemDetails devolvido pela Minimal API quando o login é recusado. */
type ProblemDetails = {
  title?: string
  detail?: string
}

/**
 * Traduz a falha do login para uma mensagem do painel: `401` são credenciais inválidas, `400` são
 * campos vazios — ambos com o detalhe que a API publicou — e a ausência de resposta é rede/host fora.
 */
function resolveLoginError(error: unknown): string {
  if (!axios.isAxiosError(error)) {
    return 'Não foi possível concluir o login. Tente novamente.'
  }

  if (!error.response) {
    return 'Falha de rede: não foi possível alcançar o Orquestrador Lucke. Verifique a conexão e a URL da API.'
  }

  const { status, data } = error.response
  const detail = (data as ProblemDetails | null)?.detail

  if (status === 401) {
    return detail ?? 'Credenciais inválidas: verifique usuário e senha.'
  }

  if (status === 400) {
    return detail ?? 'Informe usuário e senha para continuar.'
  }

  return `O servidor respondeu ${status}. Tente novamente em instantes.`
}

/** Tela de login: credenciais do operador trocadas pelo par de tokens do Orquestrador. */
function Login() {
  const { isAuthenticated, login } = useAuth()
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [isSubmitting, setIsSubmitting] = useState(false)
  const [errorMessage, setErrorMessage] = useState<string | null>(null)

  // Sessão já aberta (ex.: o operador voltou para `/login`): o painel é o destino.
  if (isAuthenticated) {
    return <Navigate to="/" replace />
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()

    if (isSubmitting) {
      return
    }

    const trimmedUsername = username.trim()

    if (!trimmedUsername || !password) {
      setErrorMessage('Informe usuário e senha para continuar.')
      return
    }

    setIsSubmitting(true)
    setErrorMessage(null)

    try {
      await login(trimmedUsername, password)
    } catch (error) {
      setErrorMessage(resolveLoginError(error))
    } finally {
      setIsSubmitting(false)
    }
  }

  const fieldClass =
    'w-full rounded-xl border border-lucke-blue-soft/40 bg-lucke-blue-darker py-2.5 pl-10 pr-3 text-sm text-white placeholder:text-lucke-blue-lighter/40 focus:border-lucke-blue-light focus:outline-none focus:ring-2 focus:ring-lucke-blue/40 disabled:cursor-not-allowed disabled:opacity-60'
  const iconClass =
    'pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-lucke-blue-light'

  return (
    <main className="flex min-h-screen items-center justify-center bg-lucke-blue-darkest p-6">
      <section className="w-full max-w-md rounded-xl border border-lucke-blue-soft/40 bg-lucke-blue-dark p-8 shadow-lucke-glow">
        <header className="mb-8 flex items-center gap-3">
          <span className="rounded-xl bg-lucke-blue/15 p-3 text-lucke-blue-light">
            <Bot size={24} aria-hidden="true" />
          </span>
          <div>
            <h1 className="text-xl font-semibold text-white">Orquestrador Lucke</h1>
            <p className="text-sm text-lucke-blue-lighter/80">Entre para acompanhar as tarefas</p>
          </div>
        </header>

        <form className="space-y-5" onSubmit={handleSubmit} noValidate>
          <div className="space-y-2">
            <label className="text-sm font-medium text-lucke-blue-lighter" htmlFor="username">
              Usuário
            </label>
            <div className="relative">
              <User className={iconClass} size={18} aria-hidden="true" />
              <input
                id="username"
                name="username"
                type="text"
                autoComplete="username"
                placeholder="seu.usuario"
                value={username}
                onChange={(event) => setUsername(event.target.value)}
                disabled={isSubmitting}
                autoFocus
                className={fieldClass}
              />
            </div>
          </div>

          <div className="space-y-2">
            <label className="text-sm font-medium text-lucke-blue-lighter" htmlFor="password">
              Senha
            </label>
            <div className="relative">
              <Lock className={iconClass} size={18} aria-hidden="true" />
              <input
                id="password"
                name="password"
                type="password"
                autoComplete="current-password"
                placeholder="••••••••"
                value={password}
                onChange={(event) => setPassword(event.target.value)}
                disabled={isSubmitting}
                className={fieldClass}
              />
            </div>
          </div>

          {errorMessage && (
            <p
              role="alert"
              className="flex items-start gap-2 rounded-xl border border-rose-400/30 bg-rose-500/10 p-3 text-sm text-rose-300"
            >
              <AlertCircle className="mt-0.5 shrink-0" size={16} aria-hidden="true" />
              <span>{errorMessage}</span>
            </p>
          )}

          <button
            type="submit"
            disabled={isSubmitting}
            className="flex w-full items-center justify-center gap-2 rounded-xl bg-lucke-blue px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-lucke-blue-light focus:outline-none focus:ring-2 focus:ring-lucke-blue/50 focus:ring-offset-2 focus:ring-offset-lucke-blue-dark disabled:cursor-not-allowed disabled:opacity-70"
          >
            {isSubmitting ? (
              <>
                <Loader2 className="animate-spin" size={18} aria-hidden="true" />
                Entrando...
              </>
            ) : (
              <>
                <LogIn size={18} aria-hidden="true" />
                Entrar
              </>
            )}
          </button>
        </form>
      </section>
    </main>
  )
}

export default Login
