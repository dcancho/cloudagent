import { useLayoutEffect, useRef, type KeyboardEvent } from 'react'
import { IconButton } from './IconButton'
import { cx } from './cx'

export interface ComposerProps {
  value: string
  onChange: (value: string) => void
  onSend: () => void
  placeholder?: string
  disabled?: boolean
  canSend?: boolean
  size?: 'xs' | 'sm' | 'md' | 'lg'
}

export function Composer({
  value,
  onChange,
  onSend,
  placeholder,
  disabled,
  canSend = true,
  size = 'md',
}: ComposerProps) {
  const fieldRef = useRef<HTMLTextAreaElement>(null)

  useLayoutEffect(() => {
    const field = fieldRef.current
    if (!field) return
    field.style.height = 'auto'
    field.style.height = `${field.scrollHeight}px`
  }, [value])

  function onKeyDown(e: KeyboardEvent<HTMLTextAreaElement>) {
    if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) {
      e.preventDefault()
      if (canSend && value.trim()) onSend()
    }
  }

  return (
    <div className={cx('lucent-composer', `lucent-composer--${size}`, disabled && 'lucent-composer--disabled')}>
      <textarea
        ref={fieldRef}
        className="lucent-composer__field"
        rows={1}
        value={value}
        placeholder={placeholder}
        disabled={disabled}
        aria-label={placeholder}
        onChange={(e) => onChange(e.target.value)}
        onKeyDown={onKeyDown}
      />
      <div className="lucent-composer__actions">
        <IconButton
          icon="arrow_upward"
          label="Send"
          variant="primary"
          size="sm"
          disabled={disabled || !canSend || !value.trim()}
          onClick={onSend}
        />
      </div>
    </div>
  )
}
