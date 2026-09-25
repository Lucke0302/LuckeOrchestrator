/// <reference types="vite/client" />

/**
 * Variáveis de ambiente do painel (somente as prefixadas com `VITE_` chegam ao
 * cliente). Documentadas em `.env.example`.
 */
interface ImportMetaEnv {
  /** URL base da API do Orquestrador Lucke (ex.: http://localhost:5000/api). */
  readonly VITE_API_BASE_URL?: string
  /**
   * URL do hub SignalR de logs (ex.: http://localhost:5000/hubs/logs). Opcional: sem ela, o painel
   * deriva o endereço do hub a partir de `VITE_API_BASE_URL`.
   */
  readonly VITE_HUB_URL?: string
}
