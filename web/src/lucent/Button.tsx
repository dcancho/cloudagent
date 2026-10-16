import type { ButtonHTMLAttributes } from 'react'
import { cx } from './cx'

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: 'primary' | 'secondary' | 'text'
  size?: 'xs' | 'sm' | 'md' | 'lg'
  danger?: boolean
}

export function Button({
  variant = 'primary',
  size = 'md',
  danger,
  className,
  type = 'button',
  children,
  ...rest
}: ButtonProps) {
  return (
    <span
      className={cx(
        'lucent-button',
        `lucent-button--${variant}`,
        `lucent-button--${size}`,
        danger && 'lucent-button--danger',
        className,
      )}
    >
      <button {...rest} type={type} className="lucent-button__inner">
        {children}
      </button>
    </span>
  )
}
