import { useEffect, useRef, useState } from 'react'
import { Button, TextInput, Icon } from '@design-foundry/lucent'

type Msg = { role: 'you' | 'agent'; text: string }

const WS_URL = (import.meta.env.VITE_AGENT_WS as string | undefined) ?? 'ws://localhost:5080/ws'

function App() {
  const [messages, setMessages] = useState<Msg[]>([])
  const [draft, setDraft] = useState('')
  const [connected, setConnected] = useState(false)
  const wsRef = useRef<WebSocket | null>(null)
  const logRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    const ws = new WebSocket(WS_URL)
    wsRef.current = ws
    ws.onopen = () => setConnected(true)
    ws.onclose = () => setConnected(false)
    ws.onmessage = (e) => setMessages((m) => [...m, { role: 'agent', text: String(e.data) }])
    return () => ws.close()
  }, [])

  useEffect(() => {
    logRef.current?.scrollTo({ top: logRef.current.scrollHeight })
  }, [messages])

  function send() {
    const text = draft.trim()
    if (!text || wsRef.current?.readyState !== WebSocket.OPEN) return
    wsRef.current.send(text)
    setMessages((m) => [...m, { role: 'you', text }])
    setDraft('')
  }

  return (
    <div className="app">
      <header className="app__bar">
        <Icon name="smart_toy" ariaLabel="CloudAgent" />
        <span className="app__title">CloudAgent</span>
        <span className={`app__status app__status--${connected ? 'on' : 'off'}`}>
          {connected ? 'connected' : 'offline'}
        </span>
      </header>

      <div className="app__log" ref={logRef}>
        {messages.length === 0 && (
          <p className="app__empty">Say hello to your agent.</p>
        )}
        {messages.map((m, i) => (
          <div key={i} className={`msg msg--${m.role}`}>
            <div className="msg__bubble">{m.text}</div>
          </div>
        ))}
      </div>

      <form
        className="app__composer"
        onSubmit={(e) => {
          e.preventDefault()
          send()
        }}
      >
        <TextInput value={draft} onChange={setDraft} placeholder="Message the agent…" />
        <Button type="submit" variant="primary" prefix={<Icon name="send" />}>
          Send
        </Button>
      </form>
    </div>
  )
}

export default App
