import axios from 'axios'
import type { AxiosError, InternalAxiosRequestConfig } from 'axios'

/**
 * URL base da API do Orquestrador Lucke — o host HTTP do daemon (webhook do GitHub,
 * `/api/auth`, `/api/tasks` e o hub de logs). Configurável por ambiente (`.env.local`);
 * sem a variável, cai no endereço local padrão de desenvolvimento.
 */
export const API_BASE_URL: string =
  import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:5000/api'

/**
 * Chave do access token JWT no `localStorage`. O contexto de autenticação (login/refresh)
 * passa a gravar e limpar esse valor; o interceptor abaixo apenas o injeta na requisição.
 */
export const ACCESS_TOKEN_STORAGE_KEY = 'lucke.accessToken'

/**
 * Chave do refresh token no `localStorage`. O `AuthContext` grava/limpa o valor no login/logout e o
 * interceptor de resposta o lê para trocar o par em `/auth/refresh` quando o access token expira.
 */
export const REFRESH_TOKEN_STORAGE_KEY = 'lucke.refreshToken'

/** Instância única do Axios usada por todos os serviços HTTP do painel. */
export const api = axios.create({
  baseURL: API_BASE_URL,
  headers: {
    'Content-Type': 'application/json',
  },
})

/**
 * Interceptor de requisição: injeta o JWT (quando existir) no cabeçalho `Authorization`.
 * É o único ponto que toca o token — nenhuma tela monta o cabeçalho à mão.
 */
api.interceptors.request.use((config: InternalAxiosRequestConfig) => {
  const accessToken = localStorage.getItem(ACCESS_TOKEN_STORAGE_KEY)

  if (accessToken) {
    config.headers.Authorization = `Bearer ${accessToken}`
  }

  return config
})

/**
 * Rota de renovação: um `401` aqui é o refresh recusado (rotacionado/expirado) e não pode disparar
 * outro refresh — seria recursão infinita.
 */
const REFRESH_ROUTE = '/auth/refresh'

/**
 * Rota de login: um `401` aqui é credencial errada, não sessão expirada — renovar não faz sentido e
 * trocaria a mensagem de erro da tela de login.
 */
const LOGIN_ROUTE = '/auth/login'

/**
 * Config da requisição com a marca de "já tentou renovar": um replay que volte a responder `401` não
 * tenta o refresh de novo — é o que impede o laço infinito.
 */
type RetriableRequestConfig = InternalAxiosRequestConfig & { _retry?: boolean }

/** Requisição à espera da renovação em voo: resolvida com o token novo ou rejeitada com a falha. */
type RefreshQueueItem = {
  resolve: (token: string) => void
  reject: (error: unknown) => void
}

/** Corpo de `POST /auth/login` e `POST /auth/refresh` (`AuthTokenResponse` da Application .NET). */
type AuthTokenResponse = {
  accessToken: string
  refreshToken: string
  accessTokenExpiresAtUtc: string
  refreshTokenExpiresAtUtc: string
}

// Estado do refresh fora do interceptor: uma única renovação atende todas as requisições que
// responderam `401` no mesmo instante (o access token de 15 min expira para a aba inteira de uma vez).
let isRefreshing = false
let failedQueue: RefreshQueueItem[] = []

/**
 * Drena a fila de espera: libera cada requisição com o token novo ou a rejeita com a falha do
 * refresh. A fila é sempre esvaziada — uma promessa pendente aqui é uma requisição travada.
 */
function processQueue(error: unknown, token: string | null = null): void {
  failedQueue.forEach(({ resolve, reject }) => {
    if (error) {
      reject(error)
    } else if (token) {
      resolve(token)
    }
  })

  failedQueue = []
}

/** Descarta o par de tokens da sessão e devolve o operador à tela de login. */
function clearSessionAndRedirectToLogin(): void {
  localStorage.removeItem(ACCESS_TOKEN_STORAGE_KEY)
  localStorage.removeItem(REFRESH_TOKEN_STORAGE_KEY)

  window.location.href = '/login'
}

/**
 * Interceptor de resposta: renova o access token expirado (`401`) e refaz a requisição original.
 *
 * A renovação é **única por vez**: a primeira requisição que recebe `401` chama `/auth/refresh` e as
 * demais entram em `failedQueue`, esperando a mesma promessa. O `POST` do refresh usa o `axios` cru
 * (e não a instância `api`) de propósito — pela instância, um refresh recusado reentraria neste mesmo
 * interceptor e viraria recursão. Sem refresh token (ou com o refresh recusado) a sessão é descartada
 * e a navegação volta para `/login`.
 */
api.interceptors.response.use(
  (response) => response,
  async (error: AxiosError) => {
    const originalRequest = error.config as RetriableRequestConfig | undefined

    if (
      error.response?.status !== 401 ||
      !originalRequest ||
      originalRequest._retry ||
      originalRequest.url === REFRESH_ROUTE ||
      originalRequest.url === LOGIN_ROUTE
    ) {
      return Promise.reject(error)
    }

    // Marca cedo: o replay (imediato ou vindo da fila) que voltar a responder `401` não renova de novo.
    originalRequest._retry = true

    if (isRefreshing) {
      // Renovação em voo: espera a mesma promessa e só então refaz a requisição com o token novo.
      return new Promise<string>((resolve, reject) => {
        failedQueue.push({ resolve, reject })
      }).then((token) => {
        originalRequest.headers.Authorization = `Bearer ${token}`

        return api(originalRequest)
      })
    }

    isRefreshing = true

    try {
      const refreshToken = localStorage.getItem(REFRESH_TOKEN_STORAGE_KEY)

      if (!refreshToken) {
        throw new Error('Sessão expirada e nenhum refresh token guardado para renovar.')
      }

      const { data } = await axios.post<AuthTokenResponse>(`${API_BASE_URL}${REFRESH_ROUTE}`, {
        refreshToken,
      })

      localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, data.accessToken)
      localStorage.setItem(REFRESH_TOKEN_STORAGE_KEY, data.refreshToken)
      api.defaults.headers.common.Authorization = `Bearer ${data.accessToken}`

      processQueue(null, data.accessToken)
    } catch (refreshError) {
      processQueue(refreshError, null)
      clearSessionAndRedirectToLogin()

      return Promise.reject(refreshError)
    } finally {
      isRefreshing = false
    }

    return api(originalRequest)
  },
)

export default api
