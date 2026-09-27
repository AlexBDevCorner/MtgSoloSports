import { useEffect, useMemo, useState } from 'react';
import { AthleteProfilePage } from './features/athletes/AthleteProfilePage';
import { useAthleteProfile } from './features/athletes/useAthleteProfile';
import { CatalogBanner } from './features/catalog/CatalogBanner';
import { useCatalogStats } from './features/catalog/catalogApi';
import { ColorCupPage } from './features/cups/ColorCupPage';
import { DashboardPage } from './features/dashboard/DashboardPage';
import { useDashboard } from './features/dashboard/useDashboard';
import { HistoryPage } from './features/history/HistoryPage';
import { LivePage } from './features/live/LivePage';
import { RecordsPage } from './features/records/RecordsPage';
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
  const [selectedAthleteId, setSelectedAthleteId] = useState<number | null>(null);
  const saves = useSaves();
  const catalog = useCatalogStats();
  const dashboard = useDashboard(view === 'dashboard' ? selectedSaveId : selectedSaveId);
  const athlete = useAthleteProfile(
    view === 'athlete' ? selectedSaveId : null,
    view === 'athlete' ? selectedAthleteId : null,
  );

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
      ) : view === 'live' ? (
        <LivePage
          saveId={selectedSaveId}
          progress={dashboard.data?.progress ?? null}
          progressLoading={dashboard.loading}
          hasSelection={selectedSaveId !== null}
          onMutated={dashboard.refresh}
          onGoToSaves={() => {
            setView('saves');
          }}
          onSelectAthlete={(athleteId) => {
            setSelectedAthleteId(athleteId);
            setView('athlete');
          }}
        />
      ) : view === 'history' ? (
        <HistoryPage
          saveId={selectedSaveId}
          hasSelection={selectedSaveId !== null}
          onGoToSaves={() => {
            setView('saves');
          }}
          onSelectAthlete={(athleteId) => {
            setSelectedAthleteId(athleteId);
            setView('athlete');
          }}
        />
      ) : view === 'records' ? (
        <RecordsPage
          saveId={selectedSaveId}
          hasSelection={selectedSaveId !== null}
          onGoToSaves={() => {
            setView('saves');
          }}
          onSelectAthlete={(athleteId) => {
            setSelectedAthleteId(athleteId);
            setView('athlete');
          }}
        />
      ) : view === 'cups' ? (
        <ColorCupPage
          saveId={selectedSaveId}
          hasSelection={selectedSaveId !== null}
          onGoToSaves={() => {
            setView('saves');
          }}
          onSelectAthlete={(athleteId) => {
            setSelectedAthleteId(athleteId);
            setView('athlete');
          }}
        />
      ) : view === 'athlete' ? (
        <AthleteProfilePage
          saveId={selectedSaveId}
          athleteId={selectedAthleteId}
          state={athlete}
          hasSelection={selectedSaveId !== null}
          onBackToLive={() => {
            setView('live');
          }}
          onGoToSaves={() => {
            setView('saves');
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
