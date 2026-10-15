import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import path from 'node:path'
import { fileURLToPath, URL } from 'node:url'

const lucentDir = process.env.LUCENT_DIR
  ? path.resolve(process.env.LUCENT_DIR)
  : fileURLToPath(new URL('../design-foundry/lucent', import.meta.url))

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@lucent/styles': path.join(lucentDir, 'styles'),
    },
  },
  server: {
    fs: { allow: ['.', lucentDir] },
  },
})
