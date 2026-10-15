import type { ButtonHTMLAttributes } from 'react'
import { Icon } from './Icon'
import { cx } from './cx'

export interface IconButtonProps extends Omit<ButtonHTMLAttributes<HTMLButtonElement>, 'children'> {
  icon: string
  label: string
  variant?: 'primary' | 'secondary' | 'text'
  size?: 'xs' | 'sm' | 'md' | 'lg'
  danger?: boolean
}

export function IconButton({
  icon,
  label,
  variant = 'text',
  size = 'md',
  danger,
  className,
  type = 'button',
  ...rest
}: IconButtonProps) {
  return (
    <span
      className={cx(
        'lucent-icon-button',
        `lucent-icon-button--${variant}`,
        `lucent-icon-button--${size}`,
        danger && 'lucent-icon-button--danger',
        className,
      )}
    >
      <button {...rest} type={type} className="lucent-icon-button__inner" aria-label={label} title={label}>
        <Icon name={icon} size={size === 'lg' ? 'lg' : 'sm'} />
      </button>
    </span>
  )
}
