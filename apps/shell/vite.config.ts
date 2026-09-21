import { fileURLToPath, URL } from 'node:url';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

const here = (relative: string): string => fileURLToPath(new URL(relative, import.meta.url));

// TAURI_ENV_DEBUG is set by the Tauri CLI for `tauri dev` / `tauri build --debug`.
const debug = Boolean(process.env.TAURI_ENV_DEBUG);

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@': here('./src'),
      // Consume the contracts from source so no `pnpm contracts:build` is needed for dev/typecheck.
      '@clubshell/contracts': here('../../packages/contracts-ts/src/index.ts'),
    },
  },
  clearScreen: false,
  envPrefix: ['VITE_', 'TAURI_ENV_'],
  server: {
    port: 1420,
    strictPort: true,
    watch: { ignored: ['**/src-tauri/**'] },
  },
  build: {
    // WebView2 evergreen runtime; chrome110 keeps top-level await and modern syntax intact.
    target: 'chrome110',
    minify: debug ? false : 'esbuild',
    sourcemap: debug,
    outDir: 'dist',
    emptyOutDir: true,
    chunkSizeWarningLimit: 600,
    rollupOptions: {
      output: {
        // Vendor groups by change frequency, so an app-only release keeps the big chunks cached by WebView2.
        manualChunks: {
          react: ['react', 'react-dom', 'react-router-dom'],
          motion: ['framer-motion'],
          i18n: ['i18next', 'react-i18next'],
          zustand: ['zustand'],
          qrcode: ['qrcode.react'],
        },
      },
    },
  },
});
