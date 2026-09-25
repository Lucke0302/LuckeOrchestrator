import axios from 'axios'
import type { InternalAxiosRequestConfig } from 'axios'

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

export default api
