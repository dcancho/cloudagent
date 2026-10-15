import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import '@lucent/styles/core.css'
import '@lucent/styles/packs/messaging.css'
import './index.css'
import App from './App.tsx'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
