import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import './shared/ui/tokens.css';
import './shared/ui/base.css';
import App from './App';

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
