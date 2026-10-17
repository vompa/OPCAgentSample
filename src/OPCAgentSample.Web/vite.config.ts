import { fileURLToPath, URL } from 'node:url';
import vue from '@vitejs/plugin-vue';
import { defineConfig } from 'vitest/config';
import { VitePWA } from 'vite-plugin-pwa';

// Das Build-Ergebnis geht nach wwwroot des Servers (lokal liefert ein Container alles aus). Für die getrennte Oberfläche
// (Azure Static Web Apps) baut WEB_OUT_DIR=dist in einen eigenen Ordner.
const serverWwwroot = fileURLToPath(new URL('../OPCAgentSample.Server/wwwroot', import.meta.url));
const outDir = process.env.WEB_OUT_DIR ?? serverWwwroot;
const serverUrl = process.env.VITE_DEV_PROXY ?? 'http://localhost:5145';

export default defineConfig({
  plugins: [
    vue(),
    VitePWA({
      strategies: 'injectManifest',
      srcDir: 'src',
      filename: 'sw.ts',
      registerType: 'autoUpdate',
      injectRegister: 'script', // eigene Datei statt Inline-Skript (CSP)
      manifest: {
        id: '/',
        name: 'Schleifwerk · KI-Leitstand',
        short_name: 'KI-Leitstand',
        description: 'OPC UA → MCP-Server → Claude: Fragen an die Halle',
        lang: 'de',
        start_url: '/',
        scope: '/',
        display: 'standalone',
        background_color: '#eeece6',
        theme_color: '#1b232d',
        icons: [
          { src: 'icons/icon-192.png', sizes: '192x192', type: 'image/png' },
          { src: 'icons/icon-512.png', sizes: '512x512', type: 'image/png' },
          {
            src: 'icons/icon-maskable-512.png',
            sizes: '512x512',
            type: 'image/png',
            purpose: 'maskable',
          },
        ],
        // Chrome und Edge zeigen mit Screenshots den ausführlichen Installieren-Dialog (wide = Desktop, narrow = Handy).
        screenshots: [
          {
            src: 'img/web-desktop.png',
            sizes: '1200x750',
            type: 'image/png',
            form_factor: 'wide',
            label: 'Leitstand am Desktop: Stationen, Antwortkarte von Claude und MCP-Aufrufe',
          },
          {
            src: 'img/web-mobile.png',
            sizes: '468x1013',
            type: 'image/png',
            form_factor: 'narrow',
            label: 'Leitstand am Handy: Antwort von Claude zur Lage in der Halle',
          },
        ],
      },
      injectManifest: {
        globPatterns: ['**/*.{js,css,html,svg,png,woff2}'],
        globIgnores: ['img/**'], // Bilder der Erklärseite laden bei Bedarf, nicht in die Offline-Hülle
      },
      devOptions: { enabled: false },
    }),
  ],
  resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
  build: {
    outDir,
    emptyOutDir: true,
    target: 'es2022',
    sourcemap: false,
  },
  server: {
    port: 5173,
    proxy: {
      '/api': { target: serverUrl, changeOrigin: false },
    },
  },
  test: {
    environment: 'jsdom',
    include: ['src/**/*.test.ts'],
    globals: false,
  },
});
