import type { HTMLAttributes } from 'react'

export type SkeletonProps = HTMLAttributes<HTMLDivElement>

/**
 * Bloco de carregamento (skeleton) genérico — a base das telas de loading do painel.
 *
 * O tamanho/formato vem de quem usa, pelo `className`: o padrão é `h-4 w-full`, então
 * um valor diferente de altura/largura ou um `rounded-full` sobrescrevem o padrão.
 *
 * ```tsx
 * <Skeleton className="h-8 w-48" />
 * <Skeleton className="h-10 w-10 rounded-full" />
 * ```
 */
export function Skeleton({ className = '', ...props }: SkeletonProps) {
  return (
    <div
      aria-hidden="true"
      className={`animate-pulse bg-blue-800/50 rounded h-4 w-full ${className}`}
      {...props}
    />
  )
}

export default Skeleton
