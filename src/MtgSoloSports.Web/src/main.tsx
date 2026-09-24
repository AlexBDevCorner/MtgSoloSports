import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './styles.css'

function App() {
  return (
    <main className="shell">
      <p className="eyebrow">MTG SOLO SPORTS</p>
      <h1>The sporting universe is waiting.</h1>
      <p>
        This is the repository seed. Universe creation, leagues, simulation and history
        arrive through the AutonomousWork roadmap.
      </p>
    </main>
  )
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
