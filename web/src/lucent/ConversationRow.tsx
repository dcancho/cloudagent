import type { ButtonHTMLAttributes } from 'react'
import { cx } from './cx'

export interface ConversationRowProps extends Omit<ButtonHTMLAttributes<HTMLButtonElement>, 'children' | 'title'> {
  title: string
  time?: string
  active?: boolean
}

export function ConversationRow({ title, time, active, className, type = 'button', ...rest }: ConversationRowProps) {
  return (
    <button
      {...rest}
      type={type}
      className={cx('lucent-conversation-row', active && 'is-active', className)}
      aria-current={active ? 'true' : undefined}
    >
      <span className="lucent-conversation-row__main">
        <span className="lucent-conversation-row__title">{title}</span>
      </span>
      {time && (
        <span className="lucent-conversation-row__meta">
          <span className="lucent-conversation-row__time">{time}</span>
        </span>
      )}
    </button>
  )
}
