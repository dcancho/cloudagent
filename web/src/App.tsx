import { useEffect, useRef, useState } from 'react'
import { Button, Composer, ConversationRow, Icon, IconButton, MessageBubble, StatusTag, TypingIndicator } from './lucent'
import { Markdown } from './Markdown'

type Msg = { role: 'user' | 'assistant'; text: string }

type Conversation = { id: string; title: string; updatedAt: string }

type Frame =
  | { type: 'message'; text: string }
  | { type: 'typing' }
  | { type: 'welcome'; conversation: string }
  | { type: 'history'; messages: Msg[] }
  | { type: 'conversations'; items: Conversation[] }

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

const NARROW = '(max-width: 720px)'
const COLLAPSED_KEY = 'cloudagent.sidebarCollapsed'

function storedCollapsed(): boolean {
  try {
    return localStorage.getItem(COLLAPSED_KEY) === '1'
  } catch {
    return false
  }
}

function storeCollapsed(collapsed: boolean) {
  try {
    if (collapsed) localStorage.setItem(COLLAPSED_KEY, '1')
    else localStorage.removeItem(COLLAPSED_KEY)
  } catch {
    // Without storage the sidebar simply opens again on reload.
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

const timeFormat = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit' })
const dateFormat = new Intl.DateTimeFormat(undefined, { day: 'numeric', month: 'short' })

function shortTime(iso: string): string {
  const date = new Date(iso)
  return date.toDateString() === new Date().toDateString() ? timeFormat.format(date) : dateFormat.format(date)
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
  const [conversations, setConversations] = useState<Conversation[]>([])
  const [current, setCurrent] = useState<string | undefined>(storedConversation)
  const [sidebarOpen, setSidebarOpen] = useState(false)
  const [sidebarCollapsed, setSidebarCollapsed] = useState(storedCollapsed)
  const [confirmingDelete, setConfirmingDelete] = useState<string | null>(null)
  const leaveAfterDelete = useRef(false)

  useEffect(() => {
    if (!sidebarOpen) return
    const close = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setSidebarOpen(false)
    }
    window.addEventListener('keydown', close)
    return () => window.removeEventListener('keydown', close)
  }, [sidebarOpen])

  function toggleSidebar() {
    if (window.matchMedia(NARROW).matches) {
      setSidebarOpen((open) => !open)
      return
    }
    setSidebarCollapsed((collapsed) => {
      storeCollapsed(!collapsed)
      return !collapsed
    })
  }

  useEffect(() => {
    const ws = new WebSocket(WS_URL)
    wsRef.current = ws
    const requestList = () => ws.send(JSON.stringify({ type: 'list' }))
    ws.onopen = () => {
      setConnected(true)
      ws.send(JSON.stringify({ type: 'hello', protocol: 2, conversation: storedConversation() }))
    }
    ws.onclose = () => setConnected(false)
    ws.onmessage = (e) => {
      const frame = parseFrame(String(e.data))
      if (frame.type === 'welcome') {
        storeConversation(frame.conversation)
        setCurrent(frame.conversation)
        requestList()
        return
      }
      if (frame.type === 'conversations') {
        setConversations(frame.items)
        if (leaveAfterDelete.current) {
          leaveAfterDelete.current = false
          storeConversation(null)
          setCurrent(undefined)
          setMessages([])
          setAgentTyping(false)
          setSession((s) => s + 1)
        }
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
        requestList()
      }
    }
    return () => {
      window.clearTimeout(typingTimer.current)
      ws.close()
    }
  }, [session])

  function openConversation(id: string | null) {
    setSidebarOpen(false)
    if (id !== null && id === current) return
    storeConversation(id)
    setCurrent(id ?? undefined)
    setMessages([])
    setAgentTyping(false)
    setSession((s) => s + 1)
  }

  function deleteConversation(id: string) {
    setConfirmingDelete(null)
    const ws = wsRef.current
    if (ws?.readyState !== WebSocket.OPEN) return
    leaveAfterDelete.current = id === current
    setConversations((list) => list.filter((c) => c.id !== id))
    ws.send(JSON.stringify({ type: 'delete', conversation: id }))
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
    <div
      className={`app${sidebarOpen ? ' app--sidebar-open' : ''}${sidebarCollapsed ? ' app--sidebar-collapsed' : ''}`}
    >
      <nav className="app__sidebar" id="conversations" aria-label="Conversations">
        <div className="app__sidebar-bar">
          <h2 className="app__sidebar-title">Conversations</h2>
          <IconButton icon="add_comment" label="New conversation" onClick={() => openConversation(null)} />
          <IconButton
            className="app__sidebar-close"
            icon="close"
            label="Close conversations"
            onClick={() => setSidebarOpen(false)}
          />
        </div>
        <div className="app__conversations">
          {conversations.map((c) =>
            confirmingDelete === c.id ? (
              <div key={c.id} className="app__conversation app__conversation--confirming">
                <span className="app__conversation-title">{c.title}</span>
                <Button size="sm" danger onClick={() => deleteConversation(c.id)} disabled={!connected}>
                  Delete
                </Button>
                <IconButton icon="close" label="Keep conversation" size="sm" onClick={() => setConfirmingDelete(null)} />
              </div>
            ) : (
              <div key={c.id} className="app__conversation">
                <ConversationRow
                  title={c.title}
                  time={shortTime(c.updatedAt)}
                  active={c.id === current}
                  onClick={() => openConversation(c.id)}
                />
                <IconButton
                  className="app__conversation-delete"
                  icon="delete"
                  label="Delete conversation"
                  size="sm"
                  onClick={() => setConfirmingDelete(c.id)}
                />
              </div>
            ),
          )}
        </div>
      </nav>
      <div className="app__scrim" onClick={() => setSidebarOpen(false)} />

      <main className="app__main">
        <header className="app__bar">
          <IconButton
            className="app__menu"
            icon="menu"
            label="Conversations"
            aria-controls="conversations"
            onClick={toggleSidebar}
          />
          <Icon name="smart_toy" />
          <h1 className="app__title">CloudAgent</h1>
          <StatusTag status={connected ? 'success' : 'neutral'}>{connected ? 'Connected' : 'Offline'}</StatusTag>
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
      </main>
    </div>
  )
}

export default App
