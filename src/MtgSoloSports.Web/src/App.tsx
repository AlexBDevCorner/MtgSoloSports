import { useEffect, useMemo, useState } from 'react';
import { CatalogBanner } from './features/catalog/CatalogBanner';
import { useCatalogStats } from './features/catalog/catalogApi';
import { DashboardPage } from './features/dashboard/DashboardPage';
import { useDashboard } from './features/dashboard/useDashboard';
import { AppShell, type View } from './features/shell/AppShell';
import { SavesPage } from './features/saves/SavesPage';
import { useSaves } from './features/saves/useSaves';

const SELECTED_KEY = 'mtg-solo-sports:selected-save';

function readSelected(): string | null {
  try {
    const value = localStorage.getItem(SELECTED_KEY);
    return value && value.length > 0 ? value : null;
  } catch {
    return null;
  }
}

export default function App() {
  const [view, setView] = useState<View>('saves');
  const [selectedSaveId, setSelectedSaveId] = useState<string | null>(() => readSelected());
  const saves = useSaves();
  const catalog = useCatalogStats();
  const dashboard = useDashboard(view === 'dashboard' ? selectedSaveId : selectedSaveId);

  useEffect(() => {
    try {
      if (selectedSaveId) {
        localStorage.setItem(SELECTED_KEY, selectedSaveId);
      } else {
        localStorage.removeItem(SELECTED_KEY);
      }
    } catch {
      // storage is best-effort; selection still works in memory
    }
  }, [selectedSaveId]);

  useEffect(() => {
    if (selectedSaveId && !saves.loading && saves.saves.length > 0) {
      const stillThere = saves.saves.some((row) => row.saveId === selectedSaveId);
      if (!stillThere) {
        setSelectedSaveId(null);
      }
    }
  }, [saves.loading, saves.saves, selectedSaveId]);

  const selectedSave = useMemo(
    () => saves.saves.find((row) => row.saveId === selectedSaveId) ?? null,
    [saves.saves, selectedSaveId],
  );

  const seasonLabel = dashboard.data
    ? `Season ${dashboard.data.detail.currentSeason}`
    : selectedSave
      ? `Season ${selectedSave.currentSeason}`
      : null;
  const stageLabel = dashboard.data
    ? dashboard.data.progress.isSeasonComplete
      ? 'Stage complete'
      : `Stage ${dashboard.data.progress.globalStage}`
    : null;

  return (
    <AppShell
      view={view}
      onNavigate={setView}
      dashboardEnabled={selectedSaveId !== null}
      saveName={dashboard.data?.detail.name ?? selectedSave?.name ?? null}
      seasonLabel={seasonLabel}
      stageLabel={stageLabel}
    >
      <CatalogBanner stats={catalog.stats} loading={catalog.loading} />
      {view === 'saves' ? (
        <SavesPage
          saves={saves}
          selectedSaveId={selectedSaveId}
          catalogSufficient={catalog.stats?.isSufficientForSave ?? false}
          onSelect={(saveId) => {
            setSelectedSaveId(saveId);
            setView('dashboard');
          }}
        />
      ) : (
        <DashboardPage
          data={dashboard.data}
          loading={dashboard.loading}
          error={dashboard.error}
          notFound={dashboard.notFound}
          hasSelection={selectedSaveId !== null}
          onRefresh={dashboard.refresh}
          onGoToSaves={() => {
            setView('saves');
          }}
        />
      )}
    </AppShell>
  );
}
