# Orquestrador Lucke — Painel Web (`web/`)

Front-end do Orquestrador Lucke: consome o host HTTP do daemon .NET (porta `5000`) — a API de
gerenciamento/revisão de tarefas (`/api/tasks`), a autenticação (`/api/auth`) e o log em tempo real
pelo hub SignalR (`/hubs/logs`).

## Stack

| Peça | Papel |
| --- | --- |
| **Vite + React 19 + TypeScript** | build, HMR e tipagem do painel |
| **Tailwind CSS 3** | estilo utilitário com a paleta corporativa `lucke-blue` (dark mode nativo) |
| **Axios** | cliente HTTP — `src/services/api.ts` injeta o JWT no `Authorization` |
| **@microsoft/signalr** | cliente do hub de logs (`/hubs/logs`, token na query string) |
| **react-router-dom** | roteamento das telas (`src/pages`) |
| **lucide-react** | ícones |

## Estrutura

```
src/
  components/ui/   componentes genéricos (Skeleton.tsx = base dos estados de carregamento)
  pages/           telas (login, dashboard) — a implementar
  services/        api.ts (Axios + interceptor de JWT) e demais clientes HTTP
  hooks/           hooks reutilizáveis
  contexts/        contexts (autenticação, tema)
```

## Scripts

```bash
npm install      # dependências
npm run dev      # servidor de desenvolvimento (http://localhost:5173)
npm run build    # tsc -b + vite build (saída em dist/)
npm run lint     # ESLint
npm run preview  # serve o build de produção
```

## Configuração

A origem do painel (`http://localhost:5173`) já está liberada no `Cors:AllowedOrigins` do daemon.
A URL da API vem da variável de ambiente `VITE_API_BASE_URL` — copie `.env.example` para `.env.local`
para apontar para outro host (sem a variável, o padrão é `http://localhost:5000/api`).
