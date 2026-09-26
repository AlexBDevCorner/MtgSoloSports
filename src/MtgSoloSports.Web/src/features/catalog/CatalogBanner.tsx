import { Notice } from '../../shared/ui/Notice';
import type { CatalogStats } from './catalogApi';

const COLOR_ORDER = [
  'White',
  'Blue',
  'Black',
  'Red',
  'Green',
  'Multicolor',
  'Hybrid',
  'Colorless',
] as const;

export function CatalogBanner({
  stats,
  loading,
}: {
  stats: CatalogStats | null;
  loading: boolean;
}) {
  if (loading) {
    return (
      <div className="catalog-strip" role="status" aria-live="polite">
        <span className="spinner" aria-hidden="true" />
        <span>Checking card catalog…</span>
      </div>
    );
  }

  if (!stats || stats.totalAthletes === 0) {
    return (
      <Notice tone="empty" title="No card catalog yet">
        <p>
          Import the creature catalog through <code>POST /api/catalog/import</code> before
          creating a save. A save needs 256 athletes per sporting color.
        </p>
      </Notice>
    );
  }

  if (!stats.isSufficientForSave) {
    return (
      <Notice tone="warn" title={`Catalog incomplete — ${stats.totalAthletes} athletes`}>
        <p>
          Every sporting color needs 256 athletes before a save can be created. Current
          coverage is listed below.
        </p>
        <CatalogCounts stats={stats} />
      </Notice>
    );
  }

  return (
    <div className="catalog-strip catalog-ready">
      <span className="dot dot-ok" aria-hidden="true" />
      <span>
        Catalog ready — <strong>{stats.totalAthletes}</strong> athletes, 8 colors at quota.
      </span>
    </div>
  );
}

export function CatalogCounts({ stats }: { stats: CatalogStats }) {
  return (
    <ul className="color-counts">
      {COLOR_ORDER.map((color) => {
        const key = color.toLowerCase();
        const count =
          stats.countsBySportingColor[color] ??
          stats.countsBySportingColor[key] ??
          stats.countsBySportingColor[color.toUpperCase()] ??
          0;
        const ok = count >= 256;
        return (
          <li key={color} className={ok ? 'ok' : 'short'}>
            <span>{color}</span>
            <strong>{count}/256</strong>
          </li>
        );
      })}
    </ul>
  );
}
