import type { ReactNode } from 'react';

export type View = 'saves' | 'dashboard' | 'live' | 'history' | 'records' | 'cups' | 'athlete';

export function AppShell({
  view,
  onNavigate,
  dashboardEnabled,
  saveName,
  seasonLabel,
  stageLabel,
  children,
}: {
  view: View;
  onNavigate: (view: View) => void;
  dashboardEnabled: boolean;
  saveName: string | null;
  seasonLabel: string | null;
  stageLabel: string | null;
  children: ReactNode;
}) {
  return (
    <div className="app">
      <header className="topbar">
        <div className="brand">
          <p className="eyebrow">MTG Solo Sports</p>
          <h1 className="brand-title">Sports database</h1>
        </div>
        <nav className="nav" aria-label="Main">
          <button
            type="button"
            className={view === 'saves' ? 'nav-item current' : 'nav-item'}
            aria-current={view === 'saves' ? 'page' : undefined}
            onClick={() => {
              onNavigate('saves');
            }}
          >
            Saves
          </button>
          <button
            type="button"
            className={view === 'dashboard' ? 'nav-item current' : 'nav-item'}
            aria-current={view === 'dashboard' ? 'page' : undefined}
            disabled={!dashboardEnabled}
            title={dashboardEnabled ? 'Open dashboard' : 'Select a save first'}
            onClick={() => {
              onNavigate('dashboard');
            }}
          >
            Dashboard
          </button>
          <button
            type="button"
            className={view === 'live' ? 'nav-item current' : 'nav-item'}
            aria-current={view === 'live' ? 'page' : undefined}
            disabled={!dashboardEnabled}
            title={dashboardEnabled ? 'Open live rounds' : 'Select a save first'}
            onClick={() => {
              onNavigate('live');
            }}
          >
            Live
          </button>
          <button
            type="button"
            className={view === 'history' ? 'nav-item current' : 'nav-item'}
            aria-current={view === 'history' ? 'page' : undefined}
            disabled={!dashboardEnabled}
            title={dashboardEnabled ? 'Browse history' : 'Select a save first'}
            onClick={() => {
              onNavigate('history');
            }}
          >
            History
          </button>
          <button
            type="button"
            className={view === 'records' ? 'nav-item current' : 'nav-item'}
            aria-current={view === 'records' ? 'page' : undefined}
            disabled={!dashboardEnabled}
            title={dashboardEnabled ? 'Browse records' : 'Select a save first'}
            onClick={() => {
              onNavigate('records');
            }}
          >
            Records
          </button>
          <button
            type="button"
            className={view === 'cups' ? 'nav-item current' : 'nav-item'}
            aria-current={view === 'cups' ? 'page' : undefined}
            disabled={!dashboardEnabled}
            title={dashboardEnabled ? 'Browse cups' : 'Select a save first'}
            onClick={() => {
              onNavigate('cups');
            }}
          >
            Cups
          </button>
        </nav>
        <div className="save-strip" aria-live="polite">
          {saveName ? (
            <>
              <span className="save-pill" title={saveName}>
                {saveName}
              </span>
              {seasonLabel ? <span className="meta-pill">{seasonLabel}</span> : null}
              {stageLabel ? <span className="meta-pill">{stageLabel}</span> : null}
            </>
          ) : (
            <span className="meta-pill muted">No save open</span>
          )}
        </div>
      </header>
      <main className="content">{children}</main>
      <footer className="foot">
        <span>Local single-user simulation · deterministic engine · dark board</span>
      </footer>
    </div>
  );
}
