import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import '@fontsource-variable/inter';
import '@fontsource-variable/unbounded';
import '@fontsource-variable/jetbrains-mono';
import '@fontsource-variable/doto';
import { App } from '@/App';
import '@/index.css';

const container = document.getElementById('root');
if (!container) {
  throw new Error('#root missing');
}
createRoot(container).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
