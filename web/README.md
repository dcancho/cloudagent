# CloudAgent Web

The web client for [CloudAgent](https://github.com/dcancho/cloudagent) — a React + Vite SPA,
built on the **Lucent** design system, that talks to the agent over WebSocket. Designed to be
wrapped with **Tauri** later for a desktop companion.

## Stack

- **React + Vite + TypeScript** — the SPA (and Tauri's happy path).
- **Lucent** — Design Foundry's design system. This app consumes the `react` kit + the
  framework-neutral CSS (`styles/lucent.css`). Lucent is unpublished (pre-1.0), so it's
  referenced **locally** via a Vite alias + a tsconfig `paths` mapping. Point `LUCENT_DIR` at
  your checkout if it isn't the default sibling layout.

## Run

```sh
npm install
npm run dev        # http://localhost:5173
```

It connects to the agent's web-chat WebSocket at `ws://localhost:5080/ws` (override with
`VITE_AGENT_WS`). So: start the agent with the web chat on (`WEBCHAT=1` in the cloudagent
repo), then run this — you'll be talking to the real agent.

## Status

First slice: a working chat against the agent's existing WebSocket surface, styled with Lucent
(app bar, message list, `TextInput` + `Button`). Next: grow into the fuller CloudAgent API
(conversation list, live agent/subagent tree, memory, todos), then wrap in Tauri.

### Lucent dogfooding notes

Gaps hit while wiring Lucent in (feedback for the kit):

- `packages/react` ships a built `dist/` but **no `package.json`** — can't be a normal
  dependency yet. Worked around with a Vite alias + tsconfig `paths` to the built `.d.ts`.
- It should declare **`react` / `react-dom` / `@floating-ui/react` as peerDependencies** (the
  app installs `@floating-ui/react` manually today).
- Components use **Material Icons** ligatures, so the consumer must load that font (done in
  `index.html`).
