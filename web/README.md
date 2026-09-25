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
  components/      ChatMessage.tsx (bolha do chat com o raciocínio em <details>) e ui/ (genéricos: Skeleton.tsx)
  contexts/        sessão: AuthContext.tsx (contexto + useAuth) e AuthProvider.tsx (login/logout)
  hooks/           useLogStream.ts (stream do hub SignalR em estado)
  pages/           Login.tsx (autenticação) e Dashboard.tsx (três colunas: tarefas, chat e logs)
  services/        api.ts (Axios + interceptor de JWT) e TaskService.ts (GET/accept/reject de /api/tasks)
```

## Autenticação

`AuthProvider` troca as credenciais por um par de tokens em `POST /api/auth/login` (pela instância do
Axios de `services/api.ts`) e guarda o par no `localStorage` sob as chaves `lucke.accessToken` e
`lucke.refreshToken` — as mesmas que `ACCESS_TOKEN_STORAGE_KEY`/`REFRESH_TOKEN_STORAGE_KEY` expõem e
que o interceptor usa para montar o cabeçalho `Authorization`. `logout()` limpa as duas chaves.

`ProtectedRoute` (em `src/App.tsx`) só entrega as rotas internas com sessão aberta: enquanto o
`AuthContext` está em `isLoading` mostra o `Skeleton` e, sem sessão, redireciona para `/login`. A
recusa do daemon (401) e a queda de rede viram mensagens em português na própria tela de login.

## Dashboard

`Dashboard.tsx` ocupa a tela inteira (`h-screen`, `bg-lucke-blue-darkest`/`text-lucke-blue-lighter`) em
três colunas, e cada uma é uma área de painel com cabeçalho próprio:

| Coluna | Conteúdo |
| --- | --- |
| **Tarefas** | `GET /api/tasks` no mount e no botão de recarregar; cada card mostra payload, status, complexidade, instante e o link do pull request. `Aceitar` (`POST /api/tasks/{id}/accept`) pede confirmação, porque o merge é irreversível pela interface; `Rejeitar` (`POST /api/tasks/{id}/reject`) abre o motivo no próprio card — é o texto que vai como comentário no PR e alimenta o medidor de frustração. Só os estados revisáveis (`Concluida` e `Falhou`) mostram as ações. |
| **Chat do Agente** | Base do chat: monta a thread com o componente `ChatMessage`, que identifica as tags `<think>...</think>` e as renderiza dentro de um `<details><summary>Pensamentos do Agente</summary>` retrátil, deixando o resto do texto como mensagem normal. O envio de mensagens fica desabilitado até o daemon expor o endpoint de chat. |
| **Logs em tempo real** | Terminal preto (`font-mono`, `overflow-y-auto`) alimentado por `useLogStream`, com hora, nível colorido, categoria, mensagem e a exceção de cada evento. O bloco acompanha a última linha e mantém no máximo `MAX_LOG_ENTRIES` (100) eventos. |

- **`services/TaskService.ts`:** `getTasks`, `acceptTask` e `rejectTask` pela instância do Axios de
  `api.ts` (o JWT entra pelo interceptor). Os tipos `Task`, `TaskComplexity` e `AgentTaskStatus` usam os
  mesmos nomes que o JSON publica (`camelCase`, enumerador como texto).
- **`hooks/useLogStream.ts`:** abre o `HubConnection` em `/hubs/logs` com o access token na
  `accessTokenFactory` e escuta `log` (um evento) e `history` (retrovisor enviado ao conectar), devolvendo
  o array de `LogStreamEntry`. Falhas da listagem e da revisão viram avisos na coluna de tarefas,
  traduzidos do `ProblemDetails` do daemon.
- **`components/ChatMessage.tsx`:** recebe `author`, `content`, `role` e `timestamp`; o parsing das tags
  acontece na renderização, sem depender de HTML cru (`dangerouslySetInnerHTML` não é usado).

## Scripts

```bash
npm install      # dependências
npm run dev      # servidor de desenvolvimento (http://localhost:5173)
npm run build    # tsc -b + vite build (saída em dist/)
npm run lint     # ESLint
npm run preview  # serve o build de produção
```

## Configuração

A origem do painel (`http://localhost:5173`) já está liberada no `Cors:AllowedOrigins` do daemon —
assim como a do `npm run preview` (`http://localhost:4173`). Outra origem (a URL da Vercel, por
exemplo) entra pela variável de ambiente do host, sem recompilar:
`Cors__AllowedOrigins="http://localhost:5173,https://painel.vercel.app"`.
A URL da API vem da variável de ambiente `VITE_API_BASE_URL` — copie `.env.example` para `.env.local`
para apontar para outro host (sem a variável, o padrão é `http://localhost:5000/api`).
A URL do hub SignalR é **derivada** da URL da API (troca o sufixo `/api` por `/hubs/logs`);
`VITE_HUB_URL` sobrepõe esse cálculo quando o hub estiver em outro endereço.
