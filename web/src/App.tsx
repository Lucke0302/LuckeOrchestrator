import { Bot } from 'lucide-react'
import { Skeleton } from './components/ui/Skeleton'

/**
 * Casca do painel: apenas valida a infraestrutura (Tailwind, paleta `lucke-blue`,
 * lucide-react e o `Skeleton`). As telas de login e dashboard entram em `src/pages`.
 */
function App() {
  return (
    <main className="flex min-h-screen items-center justify-center p-6">
      <section className="w-full max-w-xl rounded-2xl border border-lucke-blue-soft/40 bg-lucke-blue-dark/60 p-8 shadow-lucke-glow">
        <header className="mb-6 flex items-center gap-3">
          <span className="rounded-xl bg-lucke-blue/15 p-3 text-lucke-blue-light">
            <Bot size={24} aria-hidden="true" />
          </span>
          <div>
            <h1 className="text-xl font-semibold text-white">Orquestrador Lucke</h1>
            <p className="text-sm text-lucke-blue-lighter/80">
              Plataforma web em construção — infraestrutura pronta.
            </p>
          </div>
        </header>

        <div className="space-y-3" role="status" aria-label="Carregando painel">
          <Skeleton className="h-5 w-2/3" />
          <Skeleton className="h-4 w-full" />
          <Skeleton className="h-4 w-5/6" />
          <Skeleton className="h-32 w-full" />
        </div>
      </section>
    </main>
  )
}

export default App
