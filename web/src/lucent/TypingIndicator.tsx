import { cx } from './cx'

export type TypingVariant = 'typing' | 'generating'

export interface TypingIndicatorProps {
  variant?: TypingVariant
  label?: string
}

export function TypingIndicator({ variant = 'typing', label }: TypingIndicatorProps) {
  const text = label ?? (variant === 'generating' ? 'Generating…' : '')
  return (
    <div
      className={cx('lucent-typing-indicator', `lucent-typing-indicator--${variant}`)}
      role="status"
      aria-live="polite"
      aria-label={variant === 'generating' ? 'Generating' : 'Typing'}
    >
      <span className="lucent-typing-indicator__dots" aria-hidden="true">
        <span className="lucent-typing-indicator__dot" />
        <span className="lucent-typing-indicator__dot" />
        <span className="lucent-typing-indicator__dot" />
      </span>
      {text && <span className="lucent-typing-indicator__label">{text}</span>}
    </div>
  )
}
