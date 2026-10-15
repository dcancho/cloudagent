import { useEffect, useRef, useState } from 'react'
import { Composer, Icon, IconButton, MessageBubble, StatusTag, TypingIndicator } from './lucent'
import { Markdown } from './Markdown'

type Msg = { role: 'user' | 'assistant'; text: string }

type Frame =
  | { type: 'message'; text: string }
  | { type: 'typing' }
  | { type: 'welcome'; conversation: string }
  | { type: 'history'; messages: Msg[] }

const WS_URL = (import.meta.env.VITE_AGENT_WS as string | undefined) ?? 'ws://localhost:5080/ws'
const CONVERSATION_KEY = 'cloudagent.conversation'

function storedConversation(): string | undefined {
  try {
    return localStorage.getItem(CONVERSATION_KEY) ?? undefined
  } catch {
    return undefined
  }
}

function storeConversation(id: string | null) {
  try {
    if (id) localStorage.setItem(CONVERSATION_KEY, id)
    else localStorage.removeItem(CONVERSATION_KEY)
  } catch {
    // Without storage the conversation simply does not survive a reload.
  }
}

const TYPING_PING_MS = 2000
const AGENT_TYPING_MS = 6000

function parseFrame(data: string): Frame {
  if (data.startsWith('{')) {
    try {
      return JSON.parse(data) as Frame
    } catch {
      // Not JSON after all; fall through to plain text.
    }
  }
  return { type: 'message', text: data }
}

function App() {
  const [messages, setMessages] = useState<Msg[]>([])
  const [draft, setDraft] = useState('')
  const [connected, setConnected] = useState(false)
  const [agentTyping, setAgentTyping] = useState(false)
  const wsRef = useRef<WebSocket | null>(null)
  const logRef = useRef<HTMLDivElement>(null)
  const typingTimer = useRef<number | undefined>(undefined)
  const lastTypingPing = useRef(0)
  const [session, setSession] = useState(0)

  useEffect(() => {
    const ws = new WebSocket(WS_URL)
    wsRef.current = ws
    ws.onopen = () => {
      setConnected(true)
      ws.send(JSON.stringify({ type: 'hello', protocol: 2, conversation: storedConversation() }))
    }
    ws.onclose = () => setConnected(false)
    ws.onmessage = (e) => {
      const frame = parseFrame(String(e.data))
      if (frame.type === 'welcome') {
        storeConversation(frame.conversation)
        return
      }
      if (frame.type === 'history') {
        setMessages(frame.messages)
        return
      }
      if (frame.type === 'typing') {
        setAgentTyping(true)
        window.clearTimeout(typingTimer.current)
        typingTimer.current = window.setTimeout(() => setAgentTyping(false), AGENT_TYPING_MS)
        return
      }
      if (frame.type === 'message') {
        const text = frame.text
        window.clearTimeout(typingTimer.current)
        setAgentTyping(false)
        setMessages((m) => [...m, { role: 'assistant', text }])
      }
    }
    return () => {
      window.clearTimeout(typingTimer.current)
      ws.close()
    }
  }, [session])

  function newConversation() {
    storeConversation(null)
    setMessages([])
    setAgentTyping(false)
    setSession((s) => s + 1)
  }

  useEffect(() => {
    logRef.current?.scrollTo({ top: logRef.current.scrollHeight })
  }, [messages, agentTyping])

  function changeDraft(value: string) {
    setDraft(value)
    const ws = wsRef.current
    const now = Date.now()
    if (value && ws?.readyState === WebSocket.OPEN && now - lastTypingPing.current > TYPING_PING_MS) {
      ws.send(JSON.stringify({ type: 'typing' }))
      lastTypingPing.current = now
    }
  }

  function send() {
    const text = draft.trim()
    if (!text || wsRef.current?.readyState !== WebSocket.OPEN) return
    wsRef.current.send(JSON.stringify({ type: 'message', text }))
    setMessages((m) => [...m, { role: 'user', text }])
    setDraft('')
    lastTypingPing.current = 0
  }

  return (
    <div className="app">
      <header className="app__bar">
        <Icon name="smart_toy" />
        <h1 className="app__title">CloudAgent</h1>
        <StatusTag status={connected ? 'success' : 'neutral'}>{connected ? 'Connected' : 'Offline'}</StatusTag>
        <IconButton icon="add_comment" label="New conversation" onClick={newConversation} />
      </header>

      <div className="app__log" ref={logRef}>
        {messages.map((m, i) => (
          <div key={i} className={`app__row app__row--${m.role}`}>
            <MessageBubble role={m.role} continuation={messages[i + 1]?.role === m.role}>
              {m.role === 'assistant' ? <Markdown text={m.text} /> : m.text}
            </MessageBubble>
          </div>
        ))}
        {agentTyping && (
          <div className="app__row app__row--assistant">
            <TypingIndicator variant="generating" label="" />
          </div>
        )}
      </div>

      <div className="app__composer">
        <Composer
          value={draft}
          onChange={changeDraft}
          onSend={send}
          canSend={connected}
          placeholder="Message CloudAgent"
        />
      </div>
    </div>
  )
}

export default App
