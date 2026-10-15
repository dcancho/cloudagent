import { useState } from 'react'
import { IconButton } from './IconButton'

export interface CodeBlockProps {
  code: string
  language?: string
}

export function CodeBlock({ code, language }: CodeBlockProps) {
  const [copied, setCopied] = useState(false)

  async function copy() {
    await navigator.clipboard.writeText(code)
    setCopied(true)
    setTimeout(() => setCopied(false), 1500)
  }

  return (
    <div className="lucent-code-block">
      <div className="lucent-code-block__header">
        <span className="lucent-code-block__lang">{language ?? 'text'}</span>
        <IconButton
          className="lucent-code-block__copy"
          icon={copied ? 'check' : 'content_copy'}
          label={copied ? 'Copied' : 'Copy code'}
          size="xs"
          onClick={copy}
        />
      </div>
      <pre className="lucent-code-block__body">
        <code>{code}</code>
      </pre>
    </div>
  )
}
