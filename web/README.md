# CloudAgent Web

The web client for CloudAgent: a React and Vite single-page app that talks to the agent's web
chat over WebSocket. It is meant to be wrapped with Tauri later as a desktop companion.

## Stack

- **React, Vite and TypeScript.**
- **Lucent**, the Design Foundry design system, used as plain CSS. The styles are vendored in
  `src/vendor/lucent/` (Apache-2.0, see its README for the version). Lucent has no React kit, so
  `src/lucent/` holds thin React wrappers that emit its `lucent-*` classes.

## Run

Start the agent with the web chat on (`WEBCHAT=1`), then:

```sh
npm install
npm run dev
```

The app runs at `http://localhost:5173` and connects to `ws://localhost:5080/ws`; set
`VITE_AGENT_WS` to use another address.

The sidebar lists every web chat conversation, most recent first, titled by its first message.
Picking one reconnects to it and shows its history. The current conversation id is kept in local
storage, so a reload continues where you were. On narrow screens the sidebar opens from the menu
button.

## Notes on Lucent

- The `--font--primitive--sans` token names Outfit, but Lucent does not load it, so
  `index.html` does.
- Components use Material Icons ligatures, so `index.html` loads that font too.
