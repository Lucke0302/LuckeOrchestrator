import { useCallback, useEffect, useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import { ACCESS_TOKEN_STORAGE_KEY, REFRESH_TOKEN_STORAGE_KEY, api } from '../services/api'
import { AuthContext } from './AuthContext'

/**
 * Corpo devolvido por `POST /auth/login` — espelha o `AuthTokenResponse` da Application (.NET):
 * o access token curto e o refresh token rotativo, com os respectivos instantes de expiração.
 */
type AuthTokenResponse = {
  accessToken: string
  refreshToken: string
  accessTokenExpiresAtUtc: string
  refreshTokenExpiresAtUtc: string
}

type AuthProviderProps = {
  children: ReactNode
}

/**
 * Provider da sessão do painel: troca as credenciais pelo par de tokens do daemon, guarda o par no
 * `localStorage` (as chaves são as mesmas que o interceptor do Axios lê em `services/api.ts`) e
 * reflete o estado para as rotas.
 *
 * Fica em arquivo próprio, separado do contexto: um módulo que exporta componente e contexto ao
 * mesmo tempo é invalidado inteiro pelo Fast Refresh (regra `react-refresh/only-export-components`).
 */
export function AuthProvider({ children }: AuthProviderProps) {
  const [isAuthenticated, setIsAuthenticated] = useState(false)
  // Começa carregando: a primeira renderização não pode decidir entre login e painel antes de a
  // sessão persistida ser checada — sem isso o layout piscaria a tela errada por um frame.
  const [isLoading, setIsLoading] = useState(true)

  // Restauração da sessão persistida: roda uma única vez, no mount. O efeito é assíncrono de
  // propósito — hoje a checagem é uma leitura do `localStorage` (a mesma chave que o interceptor do
  // Axios lê em `services/api.ts`) e amanhã é aqui que entram a validação e a rotação do token junto
  // ao daemon. O `await` também mantém o `setState` fora do corpo síncrono do efeito, que dispararia
  // renders em cascata (regra react-hooks/set-state-in-effect).
  useEffect(() => {
    let isActive = true

    async function restoreSession() {
      await Promise.resolve()

      // Login/logout podem ter acontecido durante o yield: aí o estado já é definitivo.
      if (!isActive) {
        return
      }

      const accessToken = localStorage.getItem(ACCESS_TOKEN_STORAGE_KEY)

      setIsAuthenticated(Boolean(accessToken))
      setIsLoading(false)
    }

    void restoreSession()

    return () => {
      isActive = false
    }
  }, [])

  const login = useCallback(async (username: string, password: string) => {
    const { data } = await api.post<AuthTokenResponse>('/auth/login', { username, password })

    localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, data.accessToken)
    localStorage.setItem(REFRESH_TOKEN_STORAGE_KEY, data.refreshToken)

    setIsAuthenticated(true)
  }, [])

  const logout = useCallback(() => {
    localStorage.removeItem(ACCESS_TOKEN_STORAGE_KEY)
    localStorage.removeItem(REFRESH_TOKEN_STORAGE_KEY)

    setIsAuthenticated(false)
  }, [])

  const value = useMemo(
    () => ({ isAuthenticated, isLoading, login, logout }),
    [isAuthenticated, isLoading, login, logout],
  )

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}
