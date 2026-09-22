import { fileURLToPath, URL } from 'node:url';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

const here = (relative: string): string => fileURLToPath(new URL(relative, import.meta.url));

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@': here('./src'),
      '@clubshell/contracts': here('../../packages/contracts-ts/src/index.ts'),
    },
  },
  server: { port: 1421, strictPort: true },
  build: { target: 'es2022', outDir: 'dist', emptyOutDir: true },
});
