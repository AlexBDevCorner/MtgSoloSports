import type { ReactNode } from 'react';
import { Link } from '../routing/router';
import {
  cupsPath,
  dashboardPath,
  historyPath,
  livePath,
  recordsPath,
  savesPath,
} from '../routing/routes';

export type View = 'saves' | 'dashboard' | 'live' | 'history' | 'records' | 'cups' | 'athlete';

export function AppShell({
  view,
  saveId,
  dashboardEnabled,
  saveName,
  seasonLabel,
  stageLabel,
  children,
}: {
  view: View;
  /** Save-scoped navigation target; null on Saves/landing with no save context. */
  saveId?: string | null;
  dashboardEnabled: boolean;
  saveName: string | null;
  seasonLabel: string | null;
  stageLabel: string | null;
  children: ReactNode;
}) {
  const dashboardHref = saveId ? dashboardPath(saveId) : null;
  const liveHref = saveId ? livePath(saveId) : null;
  const historyHref = saveId ? historyPath(saveId) : null;
  const recordsHref = saveId ? recordsPath(saveId) : null;
  const cupsHref = saveId ? cupsPath(saveId) : null;
  return (
    <div className="app">
      <header className="topbar">
        <div className="brand">
          <p className="eyebrow">MTG Solo Sports</p>
          <h1 className="brand-title">Sports database</h1>
        </div>
        <nav className="nav" aria-label="Main">
          <Link
            to={savesPath()}
            className={view === 'saves' ? 'nav-item current' : 'nav-item'}
            ariaCurrent={view === 'saves' ? 'page' : undefined}
          >
            Saves
          </Link>
          {dashboardHref ? (
            <Link
              to={dashboardHref}
              className={view === 'dashboard' ? 'nav-item current' : 'nav-item'}
              ariaCurrent={view === 'dashboard' ? 'page' : undefined}
              title="Open dashboard"
            >
              Dashboard
            </Link>
          ) : (
            <span
              className="nav-item"
              aria-disabled="true"
              title="Select a save first"
            >
              Dashboard
            </span>
          )}
          {liveHref ? (
            <Link
              to={liveHref}
              className={view === 'live' ? 'nav-item current' : 'nav-item'}
              ariaCurrent={view === 'live' ? 'page' : undefined}
              title="Open the current live event"
            >
              Live event
            </Link>
          ) : (
            <span
              className="nav-item"
              aria-disabled="true"
              title="Select a save first"
            >
              Live event
            </span>
          )}
          {historyHref ? (
            <Link
              to={historyHref}
              className={view === 'history' ? 'nav-item current' : 'nav-item'}
              ariaCurrent={view === 'history' ? 'page' : undefined}
              title="Browse history"
            >
              History
            </Link>
          ) : (
            <span
              className="nav-item"
              aria-disabled="true"
              title="Select a save first"
            >
              History
            </span>
          )}
          {recordsHref ? (
            <Link
              to={recordsHref}
              className={view === 'records' ? 'nav-item current' : 'nav-item'}
              ariaCurrent={view === 'records' ? 'page' : undefined}
              title="Browse records and Hall of Fame"
            >
              Records / HoF
            </Link>
          ) : (
            <span
              className="nav-item"
              aria-disabled="true"
              title="Select a save first"
            >
              Records / HoF
            </span>
          )}
          {cupsHref ? (
            <Link
              to={cupsHref}
              className={view === 'cups' ? 'nav-item current' : 'nav-item'}
              ariaCurrent={view === 'cups' ? 'page' : undefined}
              title="Browse cups"
            >
              Cups
            </Link>
          ) : (
            <span
              className="nav-item"
              aria-disabled="true"
              title="Select a save first"
            >
              Cups
            </span>
          )}
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
