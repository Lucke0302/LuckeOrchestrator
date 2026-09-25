import { api } from './api'

/**
 * Contrato de `ChatResponse` (Application/.NET): a projeção que a API publica para o painel — o texto
 * do modelo com o raciocínio embrulhado nas tags de pensamento e a nota de tentativas no fim.
 */
type ChatResponse = {
  response: string
}

/**
 * Envia a mensagem do operador ao motor de chat (`POST /api/chat`) e devolve apenas o texto da
 * resposta do modelo — o raciocínio vem dentro das tags `<think>`, que o `ChatMessage` separa na
 * renderização.
 *
 * O JWT entra pelo interceptor da instância `api` (e a renovação automática cuida do access token
 * expirado); a falha sobe pelo Axios para a tela traduzir.
 */
export async function sendMessage(message: string): Promise<string> {
  const { data } = await api.post<ChatResponse>('/chat', { message })

  return data.response
}
