import { api } from './api'

/**
 * Nível de complexidade da tarefa — espelha o enumerador `TaskComplexity` do Domain (.NET). O JSON da
 * API publica o valor como texto (`JsonStringEnumConverter`), então as chaves daqui são as mesmas
 * strings que trafegam no fio.
 */
export type TaskComplexity = 'Baixo' | 'Medio' | 'Alto' | 'Critico'

/**
 * Estágio do ciclo de vida da tarefa — espelha o enumerador `AgentTaskStatus` do Domain (.NET).
 * `Concluida` significa "PR aberto aguardando revisão"; `Aprovada` é o estado terminal do ciclo.
 */
export type AgentTaskStatus =
  | 'Pendente'
  | 'EmExecucao'
  | 'Concluida'
  | 'Falhou'
  | 'Cancelada'
  | 'Aprovada'

/**
 * Contrato de `AgentTaskResponse` (Application/.NET): a projeção que a API publica para o painel.
 * Os nomes são os do JSON (`camelCase`), não os da entidade do Domain — o front-end não depende do
 * record interno do daemon.
 */
export type Task = {
  id: string
  payload: string
  complexidade: TaskComplexity
  status: AgentTaskStatus
  criadoEm: string
  atualizadoEm: string | null
  branch: string | null
  pullRequestUrl: string | null
}

/**
 * Teto de linhas pedido na listagem: o mesmo default documentado em `GET /api/tasks?limit=50` (o caso
 * de uso no daemon ainda corta em 200).
 */
export const TASKS_LIMIT = 50

/** Lista as tarefas da mais recente para a mais antiga (`GET /tasks`). */
export async function getTasks(limit: number = TASKS_LIMIT): Promise<Task[]> {
  const { data } = await api.get<Task[]>('/tasks', { params: { limit } })

  return data
}

/**
 * Aprova a entrega (`POST /tasks/{id}/accept`): o daemon mescla o pull request da tarefa com o token
 * administrativo e devolve a tarefa já como `Aprovada`.
 */
export async function acceptTask(id: string): Promise<Task> {
  const { data } = await api.post<Task>(`/tasks/${id}/accept`)

  return data
}

/**
 * Rejeita a entrega (`POST /tasks/{id}/reject`): o motivo fecha o pull request, alimenta o medidor de
 * frustração do agente (é o texto que o overdrive recebe) e devolve a tarefa para a fila.
 */
export async function rejectTask(id: string, motivo: string): Promise<Task> {
  const { data } = await api.post<Task>(`/tasks/${id}/reject`, { motivo })

  return data
}
