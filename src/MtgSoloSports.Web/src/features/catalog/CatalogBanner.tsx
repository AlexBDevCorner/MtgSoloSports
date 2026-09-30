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
  // The rail shows catalog status; keep any actionable notice visible while
  // a refresh (e.g. right after an import) is in flight.
  if (loading && !stats) {
    return null;
  }

  if (!stats || stats.totalAthletes === 0) {
    return (
      <Notice tone="warn" title="No card catalog yet">
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

  return null;
}
