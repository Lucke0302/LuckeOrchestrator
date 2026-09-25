import { createContext, useContext } from 'react'

/** Estado e ações de autenticação oferecidos às telas do painel. */
export type AuthContextValue = {
  /** Há sessão aberta (access token guardado em memória e no `localStorage`). */
  isAuthenticated: boolean
  /** `true` enquanto a sessão persistida ainda está sendo checada no mount. */
  isLoading: boolean
  /** Troca credenciais pelo par de tokens; propaga a falha do Axios para a tela tratar. */
  login: (username: string, password: string) => Promise<void>
  /** Descarta o par de tokens e volta ao estado anônimo. */
  logout: () => void
}

/**
 * Canal da sessão do painel. Quem alimenta este contexto é o `AuthProvider` (`./AuthProvider`), em
 * arquivo próprio: um módulo que exporta componente e contexto juntos é invalidado inteiro pelo Fast
 * Refresh (regra `react-refresh/only-export-components`).
 */
export const AuthContext = createContext<AuthContextValue | null>(null)

/** Acesso ao contexto de autenticação — falha alto quando usado fora do `AuthProvider`. */
export function useAuth(): AuthContextValue {
  const context = useContext(AuthContext)

  if (!context) {
    throw new Error('useAuth precisa estar dentro de <AuthProvider>.')
  }

  return context
}
