import type { ReactNode } from 'react'

export interface StatusTagProps {
  status: 'success' | 'neutral' | 'warning' | 'error'
  children: ReactNode
}

export function StatusTag({ status, children }: StatusTagProps) {
  return (
    <span className={`lucent-status-tag lucent-status-tag--${status}`}>
      <span>{children}</span>
    </span>
  )
}
