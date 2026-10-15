import { useEffect, useRef, useState } from 'react'
import { Composer, Icon, MessageBubble, StatusTag } from './lucent'

type Msg = { role: 'user' | 'assistant'; text: string }

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
    ws.onmessage = (e) => setMessages((m) => [...m, { role: 'assistant', text: String(e.data) }])
    return () => ws.close()
  }, [])

  useEffect(() => {
    logRef.current?.scrollTo({ top: logRef.current.scrollHeight })
  }, [messages])

  function send() {
    const text = draft.trim()
    if (!text || wsRef.current?.readyState !== WebSocket.OPEN) return
    wsRef.current.send(text)
    setMessages((m) => [...m, { role: 'user', text }])
    setDraft('')
  }

  return (
    <div className="app">
      <header className="app__bar">
        <Icon name="smart_toy" />
        <h1 className="app__title">CloudAgent</h1>
        <StatusTag status={connected ? 'success' : 'neutral'}>{connected ? 'Connected' : 'Offline'}</StatusTag>
      </header>

      <div className="app__log" ref={logRef}>
        {messages.map((m, i) => (
          <div key={i} className={`app__row app__row--${m.role}`}>
            <MessageBubble role={m.role} continuation={messages[i + 1]?.role === m.role}>
              {m.text}
            </MessageBubble>
          </div>
        ))}
      </div>

      <div className="app__composer">
        <Composer
          value={draft}
          onChange={setDraft}
          onSend={send}
          canSend={connected}
          placeholder="Message CloudAgent"
        />
      </div>
    </div>
  )
}

export default App
