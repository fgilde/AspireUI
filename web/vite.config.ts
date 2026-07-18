import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: { proxy: { "/catalog": "http://localhost:5000", "/stacks": "http://localhost:5000" } },
})
