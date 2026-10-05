import '@/pwa';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import '@fontsource-variable/inter';
import '@fontsource-variable/unbounded';
import '@fontsource-variable/jetbrains-mono';
import '@fontsource-variable/doto';
import { App } from '@/App';
import '@/index.css';

// Lite mode for a weak counter PC: no blur behind the glass panels (index.css `html[data-lite]`). Set once per console
// with localStorage `clubshell.admin.lite` = `1`; read once at start.
try {
  const lite = localStorage.getItem('clubshell.admin.lite');
  if (lite && lite !== '0') document.documentElement.setAttribute('data-lite', '');
} catch {
  // storage blocked: the full look
}

const container = document.getElementById('root');
if (!container) {
  throw new Error('#root missing');
}
createRoot(container).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
