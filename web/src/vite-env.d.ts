/// <reference types="vite/client" />

/**
 * Variáveis de ambiente do painel (somente as prefixadas com `VITE_` chegam ao
 * cliente). Documentadas em `.env.example`.
 */
interface ImportMetaEnv {
  /** URL base da API do Orquestrador Lucke (ex.: http://localhost:5000/api). */
  readonly VITE_API_BASE_URL?: string
}
