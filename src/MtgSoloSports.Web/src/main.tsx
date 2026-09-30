import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import App from './App';
import './shared/ui/tokens.css';
import './shared/ui/base.css';

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
