import { useEffect, useMemo } from 'react';
import { AthleteProfilePage } from './features/athletes/AthleteProfilePage';
import { useAthleteProfile } from './features/athletes/useAthleteProfile';
import { CatalogBanner } from './features/catalog/CatalogBanner';
import { useCatalogStats } from './features/catalog/catalogApi';
import { catalogStatus } from './features/catalog/catalogStatus';
import { ColorCupPage } from './features/cups/ColorCupPage';
import { TypeCupPage } from './features/cups/TypeCupPage';
import { DashboardPage } from './features/dashboard/DashboardPage';
import { useDashboard } from './features/dashboard/useDashboard';
import { HistoryPage } from './features/history/HistoryPage';
import { LiveEventView } from './features/live/LiveEventView';
import { LivePage } from './features/live/LivePage';
import { RecordsPage } from './features/records/RecordsPage';
import { StandingsPage } from './features/standings/StandingsPage';
import {
  Link,
  navigate,
  readLastSelectedSave,
  useBrowserRoute,
  writeLastSelectedSave,
} from './features/routing/router';
import {
  dashboardPath,
  historyPath,
  livePath,
  routeSaveId as getRouteSaveId,
  savesPath,
  standingsLeaguePath,
  standingsPath,
  type Route,
} from './features/routing/routes';
import { AppShell, type View } from './features/shell/AppShell';
import { SavesPage } from './features/saves/SavesPage';
import { useSaves } from './features/saves/useSaves';
import { Loading, Notice } from './shared/ui/Notice';

function viewForRoute(route: Route): View {
  switch (route.name) {
    case 'dashboard':
      return 'dashboard';
    case 'live':
      return 'live';
    case 'standings':
      return 'standings';
    case 'history':
      return 'history';
    case 'records':
      return 'records';
    case 'cups':
      return 'cups';
    case 'athlete':
    case 'invalidAthlete':
      return 'athlete';
    default:
      return 'saves';
  }
}

function RootResolver({ fallbackSaveId }: { fallbackSaveId: string | null }) {
  useEffect(() => {
    if (fallbackSaveId) {
      navigate(dashboardPath(fallbackSaveId), { replace: true });
    } else {
      navigate(savesPath(), { replace: true });
    }
  }, [fallbackSaveId]);
  return <Loading label="Opening…" />;
}

export default function App() {
  const route = useBrowserRoute();
  const routeSaveId: string | null = getRouteSaveId(route);
  const saves = useSaves();
  const catalog = useCatalogStats();

  // Route save IDs are authoritative. The last-selected value is only a
  // fallback for the root entry flow and never overrides an explicit URL.
  // No storage-event subscription: tabs never redirect each other.
  useEffect(() => {
    if (routeSaveId) {
      writeLastSelectedSave(routeSaveId);
    }
  }, [routeSaveId]);

  const lastSelected = useMemo(() => readLastSelectedSave(), [route]);
  const dashboardSaveId = routeSaveId;
  const dashboard = useDashboard(dashboardSaveId);

  const athleteRoute =
    route.name === 'athlete'
      ? route
      : route.name === 'invalidAthlete'
        ? { saveId: route.saveId, athleteId: null as number | null, raw: route.rawAthleteId }
        : null;
  const athlete = useAthleteProfile(
    athleteRoute ? athleteRoute.saveId : null,
    athleteRoute && 'athleteId' in athleteRoute && typeof athleteRoute.athleteId === 'number'
      ? athleteRoute.athleteId
      : route.name === 'athlete'
        ? route.athleteId
        : null,
  );

  const selectedSave = useMemo(
    () =>
      routeSaveId ? (saves.saves.find((row) => row.saveId === routeSaveId) ?? null) : null,
    [saves.saves, routeSaveId],
  );

  const seasonLabel = dashboard.data
    ? `Season ${dashboard.data.detail.currentSeason}`
    : selectedSave
      ? `Season ${selectedSave.currentSeason}`
      : null;
  const stageLabel = dashboard.data
    ? dashboard.data.progress.isSeasonComplete
      ? 'Stage complete'
      : `Stage ${dashboard.data.progress.globalStage}/32`
    : null;

  const saveKnownMissing =
    !saves.loading &&
    !saves.error &&
    routeSaveId !== null &&
    !saves.saves.some((row) => row.saveId === routeSaveId);

  function renderSaveMissing(saveId: string) {
    return (
      <Notice tone="error" title="Save unavailable">
        <p>Save “{saveId}” no longer exists. Pick another universe on the Saves page.</p>
        <p>
          <Link to={savesPath()} className="primary-button">
            Back to saves
          </Link>
        </p>
      </Notice>
    );
  }

  function renderBody(): React.ReactNode {
    if (route.name === 'root') {
      return <RootResolver fallbackSaveId={lastSelected} />;
    }
    if (route.name === 'saves') {
      return (
        <SavesPage
          saves={saves}
          activeSaveId={lastSelected ?? routeSaveId}
          catalogSufficient={catalog.stats?.isSufficientForSave ?? false}
          catalogStats={catalog.stats}
          onCatalogImported={catalog.refresh}
          onOpenSave={(saveId) => {
            navigate(dashboardPath(saveId));
          }}
        />
      );
    }
    if (route.name === 'notFound') {
      return (
        <Notice tone="error" title="Page not found">
          <p>“{route.path}” does not match a known screen. Save-scoped pages live under `/saves/:saveId/…`.</p>
          <p className="live-buttons">
            <Link to={savesPath()} className="primary-button">
              Back to saves
            </Link>{' '}
            {lastSelected ? (
              <Link to={dashboardPath(lastSelected)} className="ghost-button">
                Last opened save
              </Link>
            ) : null}
          </p>
        </Notice>
      );
    }

    const saveId = routeSaveId;
    if (!saveId) {
      return (
        <Notice tone="error" title="Page not found">
          <p>This link needs a save identifier.</p>
          <p>
            <Link to={savesPath()} className="primary-button">
              Back to saves
            </Link>
          </p>
        </Notice>
      );
    }
    if (saveKnownMissing) {
      return renderSaveMissing(saveId);
    }

    switch (route.name) {
      case 'dashboard':
        return (
          <DashboardPage
            saveId={saveId}
            data={dashboard.data}
            loading={dashboard.loading}
            error={dashboard.error}
            notFound={dashboard.notFound}
            onRefresh={dashboard.refresh}
          />
        );
      case 'live': {
        const status = dashboard.data?.status ?? null;
        const liveEvent = route.event ?? dashboard.data?.status?.eventProgress?.event ?? null;
        const eventSeason =
          route.eventSeason ?? status?.eventProgress?.sourceSeasonNumber ?? status?.sourceSeasonNumber ?? null;
        if (liveEvent && eventSeason !== null) {
          return (
            <LiveEventView
              key={`${liveEvent}:${eventSeason}`}
              saveId={saveId}
              event={liveEvent}
              season={eventSeason}
              progress={status?.eventProgress?.event === liveEvent ? status.eventProgress : null}
              urlGroup={route.group}
              urlRound={route.round}
              onSelectRound={(group, round) => {
                navigate(livePath(saveId, { event: liveEvent, season: eventSeason, group, round }), { replace: true });
              }}
              onMutated={dashboard.refresh}
            />
          );
        }
        return (
          <LivePage
            saveId={saveId}
            progress={dashboard.data?.progress ?? null}
            progressLoading={dashboard.loading}
            progressNotFound={dashboard.notFound}
            progressError={dashboard.error}
            onMutated={dashboard.refresh}
            urlLeagueId={route.leagueId}
            urlRound={route.round}
            onLeagueChange={(leagueId) => {
              navigate(livePath(saveId, { league: leagueId }));
            }}
            onRoundChange={(round) => {
              const currentLeague = route.leagueId;
              navigate(
                currentLeague !== null
                  ? livePath(saveId, { league: currentLeague, round })
                  : livePath(saveId, { round }),
              );
            }}
          />
        );
      }
      case 'history':
        return (
          <HistoryPage
            saveId={saveId}
            urlSeason={route.season}
            urlCompetition={route.competitionId}
            urlStage={route.stage}
            urlRound={route.round}
            onHistoryChange={(selection) => {
              navigate(
                historyPath(saveId, {
                  season: selection.season,
                  competition: selection.competition,
                  stage: selection.stage,
                  round: selection.round,
                }),
              );
            }}
          />
        );
      case 'standings':
        return (
          <StandingsPage
            saveId={saveId}
            urlLeagueId={route.leagueId}
            urlSeason={route.season}
            urlView={route.view}
            onStandingsChange={(selection) => {
              navigate(
                selection.league !== null
                  ? standingsLeaguePath(saveId, selection.league, {
                      season: selection.season,
                      view: selection.view,
                    })
                  : standingsPath(saveId, {
                      league: selection.league,
                      season: selection.season,
                      view: selection.view,
                    }),
              );
            }}
          />
        );
      case 'records':
        return <RecordsPage saveId={saveId} />;
      case 'cups':
        return (
          <div className="dashboard">
            <section className="page-section" aria-labelledby="cups-color">
              <h2 id="cups-color" className="section-title">
                Color Cup
              </h2>
              <ColorCupPage saveId={saveId} />
            </section>
            <section className="page-section" aria-labelledby="cups-type">
              <h2 id="cups-type" className="section-title">
                Type Cup
              </h2>
              <TypeCupPage saveId={saveId} />
            </section>
          </div>
        );
      case 'athlete':
        return (
          <AthleteProfilePage
            saveId={saveId}
            athleteId={route.athleteId}
            rawAthleteId={route.rawAthleteId}
            state={athlete}
          />
        );
      case 'invalidAthlete':
        return (
          <AthleteProfilePage
            saveId={saveId}
            athleteId={null}
            rawAthleteId={route.rawAthleteId}
            state={athlete}
          />
        );
      default:
        return (
          <Notice tone="error" title="Page not found">
            <p>This link is not recognized.</p>
            <p>
              <Link to={savesPath()} className="primary-button">
                Back to saves
              </Link>
            </p>
          </Notice>
        );
    }
  }

  return (
    <AppShell
      view={viewForRoute(route)}
      saveId={routeSaveId ?? lastSelected}
      saveName={dashboard.data?.detail.name ?? selectedSave?.name ?? null}
      seasonLabel={seasonLabel}
      stageLabel={stageLabel}
      catalog={catalogStatus(catalog.stats, catalog.loading)}
    >
      <CatalogBanner stats={catalog.stats} loading={catalog.loading} onImported={catalog.refresh} />
      {renderBody()}
    </AppShell>
  );
}
