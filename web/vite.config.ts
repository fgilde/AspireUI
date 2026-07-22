import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { execSync } from 'node:child_process'

// Baked in at build time from git. Version = 0.1.<commit-count> (a readable, monotonically-increasing
// build number); build detail = short SHA + date for the tooltip. Falls back outside a git checkout.
function git(cmd: string, fallback: string): string {
  try { return execSync(cmd).toString().trim() } catch { return fallback }
}
const APP_VERSION = `0.1.${git('git rev-list --count HEAD', '0')}`
const BUILD = `${git('git rev-parse --short HEAD', 'dev')} · ${new Date().toISOString().slice(0, 10)}`

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  define: { __APP_VERSION__: JSON.stringify(APP_VERSION), __BUILD__: JSON.stringify(BUILD) },
  server: { proxy: { "/catalog": "http://localhost:5000", "/stacks": "http://localhost:5000", "/auth": "http://localhost:5000", "/env": "http://localhost:5000", "/users": "http://localhost:5000", "/settings": "http://localhost:5000", "/templates": "http://localhost:5000" } },
})
