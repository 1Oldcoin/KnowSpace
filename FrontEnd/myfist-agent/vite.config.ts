import { fileURLToPath, URL } from 'node:url'

import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import vueDevTools from 'vite-plugin-vue-devtools'

// https://vite.dev/config/
export default defineConfig({
  plugins: [
    vue(),
    vueDevTools(),
  ],
  // 桌面版通过 http://127.0.0.1:<port>/ 直接加载 dist，必须使用相对路径。
  base: './',
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  server: {
    port: 5173,
    strictPort: true,
    // 开发时前端与后端不同源，代理到本地 API；生产发布后无需代理。
    proxy: {
      '/api': { target: 'http://127.0.0.1:5056', changeOrigin: true },
      '/mcp': { target: 'http://127.0.0.1:5056', changeOrigin: true },
      '/openapi': { target: 'http://127.0.0.1:5056', changeOrigin: true },
      '/scalar': { target: 'http://127.0.0.1:5056', changeOrigin: true },
    },
  },
})
