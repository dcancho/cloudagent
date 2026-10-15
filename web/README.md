# CloudAgent Web

The web client for [CloudAgent](https://github.com/dcancho/cloudagent) — a React + Vite SPA,
built on the **Lucent** design system, that talks to the agent over WebSocket. Designed to be
wrapped with **Tauri** later for a desktop companion.

## Stack

- **React + Vite + TypeScript** — the SPA (and Tauri's happy path).
- **Lucent**: Design Foundry's design system, consumed as framework-neutral CSS
  (`styles/core.css` + `styles/packs/messaging.css`). Lucent has no React kit, so
  `src/lucent/` holds thin React wrappers that emit the `lucent-*` class contract. The CSS is
  referenced locally through a Vite alias; point `LUCENT_DIR` at your checkout if it isn't the
  sibling `../design-foundry/lucent`.

## Run

```sh
npm install
npm run dev        # http://localhost:5173
```

It connects to the agent's web-chat WebSocket at `ws://localhost:5080/ws` (override with
`VITE_AGENT_WS`). So: start the agent with the web chat on (`WEBCHAT=1` in the cloudagent
repo), then run this — you'll be talking to the real agent.

## Status

First slice: a working chat against the agent's existing WebSocket surface, built from
Lucent's message bubble, composer, status tag and icon. Next: grow into the fuller CloudAgent API
(conversation list, live agent/subagent tree, memory, todos), then wrap in Tauri.

### Lucent dogfooding notes

- The `--font--primitive--sans` token names **Outfit**, but nothing in the kit loads it; the
  consumer has to (done in `index.html`).
- Components use **Material Icons** ligatures, so the consumer must load that font too.
