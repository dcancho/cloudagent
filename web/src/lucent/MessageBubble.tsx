import type { ReactNode } from 'react'
import { cx } from './cx'

export interface MessageBubbleProps {
  role: 'user' | 'assistant' | 'system' | 'other'
  continuation?: boolean
  author?: string
  time?: string
  children: ReactNode
}

export function MessageBubble({ role, continuation, author, time, children }: MessageBubbleProps) {
  return (
    <div
      className={cx(
        'lucent-message-bubble',
        `lucent-message-bubble--${role}`,
        `lucent-message-bubble--${continuation ? 'continuation' : 'standalone'}`,
      )}
    >
      {(author || time) && (
        <div className="lucent-message-bubble__meta">
          {author && <span className="lucent-message-bubble__author">{author}</span>}
          {time && <span className="lucent-message-bubble__time">{time}</span>}
        </div>
      )}
      <div className="lucent-message-bubble__body">{children}</div>
    </div>
  )
}
