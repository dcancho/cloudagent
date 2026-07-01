import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import path from 'node:path'
import { fileURLToPath, URL } from 'node:url'

// Lucent is referenced locally (unpublished until v1.0.0). Point LUCENT_DIR at the
// design-foundry/lucent checkout; defaults to the sibling layout on this machine.
const lucentDir = process.env.LUCENT_DIR
  ? path.resolve(process.env.LUCENT_DIR)
  : fileURLToPath(new URL('../repos/design-foundry/lucent', import.meta.url))

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@design-foundry/lucent': path.join(lucentDir, 'packages/react/dist/index.js'),
      '@lucent/styles': path.join(lucentDir, 'styles/lucent.css'),
    },
    // The Lucent kit imports bare "react"; make sure it resolves to ours (no dual React).
    dedupe: ['react', 'react-dom', '@floating-ui/react'],
  },
  server: {
    // Allow importing Lucent's CSS/dist from outside the project root.
    fs: { allow: ['..', lucentDir] },
  },
})
