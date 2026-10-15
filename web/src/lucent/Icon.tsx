import { cx } from './cx'

export type IconSize = 'xs' | 'sm' | 'md' | 'lg' | 'xl'

export interface IconProps {
  name: string
  size?: IconSize
  label?: string
  className?: string
}

export function Icon({ name, size = 'md', label, className }: IconProps) {
  return (
    <span
      className={cx('lucent-icon', `lucent-icon--${size}`, className)}
      role={label ? 'img' : undefined}
      aria-label={label}
      aria-hidden={label ? undefined : true}
    >
      <span className="material-icons lucent-icon__glyph">{name}</span>
    </span>
  )
}
