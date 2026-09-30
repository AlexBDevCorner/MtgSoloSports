import type { ReactNode } from 'react';
import type { CatalogStatus } from '../catalog/catalogStatus';
import { Link } from '../routing/router';
import {
  cupsPath,
  dashboardPath,
  historyPath,
  livePath,
  recordsPath,
  savesPath,
  standingsPath,
} from '../routing/routes';
import { MenuButton } from './MenuButton';
import { RAIL_ID, useRailDrawer } from './useRailDrawer';

export type View = 'saves' | 'dashboard' | 'live' | 'standings' | 'history' | 'records' | 'cups' | 'athlete';

const VIEW_TITLES: Record<View, string> = {
  saves: 'Saves',
  dashboard: 'Dashboard',
  live: 'Live',
  standings: 'Standings',
  history: 'History',
  records: 'Records',
  cups: 'Cups',
  athlete: 'Athlete',
};

/** Rail entry: a real link when its target exists, otherwise a disabled label. */
function RailLink({
  href,
  current,
  title,
  children,
}: {
  href: string | null;
  current: boolean;
  title: string;
  children: ReactNode;
}) {
  if (!href) {
    return (
      <span className="rail-link" aria-disabled="true" title="Select a save first">
        {children}
      </span>
    );
  }
  return (
    <Link
      to={href}
      className={current ? 'rail-link current' : 'rail-link'}
      ariaCurrent={current ? 'page' : undefined}
      title={title}
    >
      {children}
    </Link>
  );
}

export function AppShell({
  view,
  saveId,
  saveName,
  seasonLabel,
  stageLabel,
  catalog,
  children,
}: {
  view: View;
  /** Save-scoped navigation target; null on Saves/landing with no save context. */
  saveId?: string | null;
  saveName: string | null;
  seasonLabel: string | null;
  stageLabel: string | null;
  catalog: CatalogStatus;
  children: ReactNode;
}) {
  const drawer = useRailDrawer();
  const scoped = (build: (id: string) => string): string | null => (saveId ? build(saveId) : null);
  const saveMeta = [seasonLabel, stageLabel].filter((part): part is string => part !== null).join(' · ');
  return (
    <div className={drawer.open ? 'app rail-open' : 'app'}>
      <aside
        id={RAIL_ID}
        className="rail"
        aria-label="Application"
        onClickCapture={(event) => {
          if ((event.target as HTMLElement).closest('a')) {
            drawer.close();
          }
        }}
      >
        <div className="rail-brand">
          <span className="rail-brand-mark" aria-hidden="true" />
          MTG Solo Sports
        </div>
        <div className="rail-save" aria-live="polite">
          {saveName ? (
            <>
              <span className="rail-save-name" title={saveName}>
                {saveName}
              </span>
              {saveMeta ? <span className="rail-save-meta">{saveMeta}</span> : null}
              <Link to={savesPath()} className="rail-save-switch">
                Switch save
              </Link>
            </>
          ) : (
            <>
              <span className="rail-save-name muted">No save open</span>
              <Link to={savesPath()} className="rail-save-switch">
                Open a save
              </Link>
            </>
          )}
        </div>
        <nav className="rail-nav" aria-label="Main">
          <RailLink href={scoped(dashboardPath)} current={view === 'dashboard'} title="Open dashboard">
            Dashboard
          </RailLink>
          <RailLink href={scoped(livePath)} current={view === 'live'} title="Open the current live event">
            Live
          </RailLink>
          <RailLink
            href={scoped(standingsPath)}
            current={view === 'standings'}
            title="Open league tables and stage placements"
          >
            Standings
          </RailLink>
          <RailLink href={scoped(historyPath)} current={view === 'history'} title="Browse history">
            History
          </RailLink>
          <RailLink href={scoped(recordsPath)} current={view === 'records'} title="Browse records and Hall of Fame">
            Records
          </RailLink>
          <RailLink href={scoped(cupsPath)} current={view === 'cups'} title="Browse cups">
            Cups
          </RailLink>
        </nav>
        <div className="rail-nav rail-nav-bottom">
          <RailLink href={savesPath()} current={view === 'saves'} title="Manage saves">
            Saves
          </RailLink>
          <p className="rail-catalog" role="status">
            <span className={`dot dot-${catalog.tone}`} aria-hidden="true" />
            {catalog.label}
          </p>
        </div>
      </aside>
      <div className="rail-backdrop" aria-hidden="true" onClick={drawer.dismiss} />
      <div className="main" inert={drawer.open}>
        <div className="topbar-mobile">
          <MenuButton open={drawer.open} onToggle={drawer.toggle} toggleRef={drawer.toggleRef} />
          <span className="topbar-mobile-brand">MTG Solo Sports</span>
          {saveName ? (
            <span className="topbar-mobile-save" title={saveName}>
              {saveName}
            </span>
          ) : null}
        </div>
        <header className="page-header">
          <h1 className="page-title">{VIEW_TITLES[view]}</h1>
        </header>
        <main className="content">{children}</main>
      </div>
    </div>
  );
}
