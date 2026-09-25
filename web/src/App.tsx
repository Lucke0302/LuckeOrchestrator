import { BrowserRouter, Navigate, Outlet, Route, Routes } from 'react-router-dom'
import { Skeleton } from './components/ui/Skeleton'
import { useAuth } from './contexts/AuthContext'
import { AuthProvider } from './contexts/AuthProvider'
import Login from './pages/Login'

/**
 * Rota privada do painel: só entrega as telas internas com sessão aberta. Enquanto o `AuthContext`
 * checa o token persistido (`isLoading`), mostra o bloco de carregamento no centro; sem sessão,
 * redireciona para `/login` (o acesso direto a uma rota interna nunca vaza a tela).
 */
function ProtectedRoute() {
  const { isAuthenticated, isLoading } = useAuth()

  if (isLoading) {
    return (
      <main className="flex min-h-screen items-center justify-center bg-lucke-blue-darkest p-6">
        <div className="w-full max-w-sm space-y-3" role="status" aria-label="Verificando sessão">
          <Skeleton className="h-6 w-1/2" />
          <Skeleton className="h-4 w-full" />
          <Skeleton className="h-4 w-5/6" />
        </div>
      </main>
    )
  }

  if (!isAuthenticated) {
    return <Navigate to="/login" replace />
  }

  return <Outlet />
}

/**
 * Casca do painel: provê a sessão (`AuthProvider`) e roteia as telas. As páginas ficam em
 * `src/pages` — por ora só o login; o dashboard substitui o placeholder de `/`.
 */
function App() {
  return (
    <AuthProvider>
      <BrowserRouter>
        <Routes>
          <Route path="/login" element={<Login />} />
          <Route element={<ProtectedRoute />}>
            <Route
              path="/"
              element={
                <main className="flex min-h-screen items-center justify-center bg-lucke-blue-darkest p-6">
                  <div>Dashboard do Orquestrador (Em Breve)</div>
                </main>
              }
            />
          </Route>
        </Routes>
      </BrowserRouter>
    </AuthProvider>
  )
}

export default App
