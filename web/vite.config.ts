import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { execSync } from 'node:child_process'

// Real build stamp: short git SHA + UTC date, baked in at build time. Falls back gracefully outside
// a git checkout (e.g. some CI/container builds).
function buildStamp(): string {
  let sha = 'dev'
  try { sha = execSync('git rev-parse --short HEAD').toString().trim() } catch { /* no git */ }
  return `${sha} · ${new Date().toISOString().slice(0, 10)}`
}

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  define: { __BUILD__: JSON.stringify(buildStamp()) },
  server: { proxy: { "/catalog": "http://localhost:5000", "/stacks": "http://localhost:5000", "/auth": "http://localhost:5000", "/env": "http://localhost:5000", "/users": "http://localhost:5000", "/settings": "http://localhost:5000", "/templates": "http://localhost:5000" } },
})
