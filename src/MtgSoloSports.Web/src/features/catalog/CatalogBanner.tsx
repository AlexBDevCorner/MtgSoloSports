import { Notice } from '../../shared/ui/Notice';
import type { CatalogStats } from './catalogApi';
import { CatalogCounts } from './CatalogCounts';
import { ScryfallImportPanel } from './ScryfallImportPanel';

export function CatalogBanner({
  stats,
  loading,
  onImported,
}: {
  stats: CatalogStats | null;
  loading: boolean;
  onImported: () => void;
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
          A save needs 256 athletes per sporting color. Import cards directly from Scryfall with
          one action — no file download or API call needed.
        </p>
        <ScryfallImportPanel stats={stats} onImported={onImported} idPrefix="catalog-banner" />
      </Notice>
    );
  }

  if (!stats.isSufficientForSave) {
    return (
      <Notice tone="warn" title={`Catalog incomplete — ${stats.totalAthletes} athletes`}>
        <p>
          Every sporting color needs 256 athletes before a save can be created. Current coverage
          is listed below. Top up the shared catalog from Scryfall with one action.
        </p>
        <CatalogCounts stats={stats} />
        <ScryfallImportPanel stats={stats} onImported={onImported} idPrefix="catalog-banner" />
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
