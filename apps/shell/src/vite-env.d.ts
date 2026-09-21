/// <reference types="vite/client" />

// Global (script) declaration file: no imports/exports so the interfaces below merge with the
// ambient `ImportMetaEnv` / `Window` declarations. `*.css`, `*.png`, `*.svg`, `*.mp4`, `*.webm`
// module shapes already come from `vite/client`.

interface ImportMetaEnv {
  /** "1" → run against the in-browser mock registry instead of Tauri IPC (Vite dev / Playwright). */
  readonly VITE_MOCK?: string;
  /** Central/mock server base URL for dev-only links (QR pages, images), e.g. http://localhost:8080. */
  readonly VITE_SERVER_URL?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}

interface Window {
  /** Injected by the Tauri runtime; presence means the page runs inside the shell webview. */
  __TAURI_INTERNALS__?: unknown;
}
