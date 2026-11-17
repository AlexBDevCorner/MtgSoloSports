import { useEffect, useMemo } from 'react';
import { AthleteProfilePage } from './features/athletes/AthleteProfilePage';
import { AthleteSearchPage } from './features/athletes/AthleteSearchPage';
import { useAthleteProfile } from './features/athletes/useAthleteProfile';
import { CatalogBanner } from './features/catalog/CatalogBanner';
import { useCatalogStats } from './features/catalog/catalogApi';
import { catalogStatus } from './features/catalog/catalogStatus';
import { CupEditionPage } from './features/cups/CupEditionPage';
import { CupSelectionView } from './features/cups/CupSelectionView';
import { CupsHubPage } from './features/cups/CupsHubPage';
import { CupTeamPage } from './features/cups/CupTeamPage';
import { DashboardPage } from './features/dashboard/DashboardPage';
import { useDashboard } from './features/dashboard/useDashboard';
import { selectionForAction } from './features/events/eventModel';
import { HistoryPage } from './features/history/HistoryPage';
import { LeagueProfilePage } from './features/leagues/LeagueProfilePage';
import { LeaguesOverviewPage } from './features/leagues/LeaguesOverviewPage';
import { LiveEventView } from './features/live/LiveEventView';
import { ChaosCupLiveView } from './features/live/ChaosCupLiveView';
import { LiveQualifierView } from './features/live/LiveQualifierView';
import { LiveDashboardCta } from './features/live/LiveDashboardCta';
import { LivePage } from './features/live/LivePage';
import { activeLivePath, resolveActiveLiveTarget } from './features/live/activeLiveEvent';
import { seasonFlow } from './features/dashboard/seasonFlowSteps';
import { QualifierDetailPage, QualifiersPage } from './features/qualifiers/QualifiersPage';
import { RecordsPage } from './features/records/RecordsPage';
import { ReportsPage } from './features/reports/ReportsPage';
import { StandingsPage } from './features/standings/StandingsPage';
import { TransitionRevealView } from './features/transitions/TransitionRevealView';
import {
  Link,
  navigate,
  readLastSelectedSave,
  useBrowserRoute,
  writeLastSelectedSave,
} from './features/routing/router';
import {
  athletesPath,
  chaosLivePath,
  dashboardPath,
  historyPath,
  livePath,
  qualifierLivePath,
  qualifiersPath,
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
import { StageCompletionBarrier } from './features/dashboard/StageCompletionBarrier';
import { useStageCompletionGuard } from './features/dashboard/useStageCompletionGuard';

function viewForRoute(route: Route): View {
  switch (route.name) {
    case 'dashboard':
      return 'dashboard';
    case 'live':
      return 'live';
    case 'standings':
      return 'standings';
    case 'leagues':
      return 'leagues';
    case 'qualifiers':
      return 'qualifiers';
    case 'history':
      return 'history';
    case 'records':
      return 'records';
    case 'reports':
      return 'reports';
    case 'cups':
      return 'cups';
    case 'athletes':
      return 'athletes';
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

  /**
   * MSS-124 Dashboard stage-completion guard. Owned here (above the shell and
   * the Dashboard) so the busy overlay, the shell lock and the router blocker
   * observe one stable operation from mutation start through the authoritative
   * refresh. See useStageCompletionGuard for the stale-closure and
   * generation-corrected lifecycle.
   */
  const stageGuard = useStageCompletionGuard({
    saveId: dashboardSaveId,
    data: dashboard.data,
    loading: dashboard.loading,
    error: dashboard.error,
    revision: dashboard.revision,
    refresh: dashboard.refresh,
  });
  const stageOp = stageGuard.stageOp;
  const stageRefreshError = stageGuard.stageRefreshError;
  const stageLocked = stageGuard.stageLocked;
  const handleStageStart = stageGuard.handleStageStart;
  const handleStageSettled = stageGuard.handleStageSettled;
  const handleDashboardRefresh = stageGuard.handleDashboardRefresh;
  const handleStageMutationError = stageGuard.handleStageMutationError;
  const handleRetryStageRefresh = stageGuard.handleRetryStageRefresh;

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

  // MSS-143: the sidebar Live entry opens the currently active competition.
  // The bare Live page resolves the same target below; the href stays on Live
  // (never a silent Dashboard redirect) so the view stays `live` while the
  // content shows the active event or a Dashboard CTA when nothing is playable.
  const liveHref = useMemo(() => {
    const progress = dashboard.data?.progress ?? null;
    const status = dashboard.data?.status ?? null;
    if (!routeSaveId || !progress || dashboard.loading || dashboard.error) {
      return null;
    }
    try {
      return activeLivePath(routeSaveId, resolveActiveLiveTarget(progress, status));
    } catch {
      return null;
    }
  }, [routeSaveId, dashboard.data, dashboard.loading, dashboard.error]);

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
            onRefresh={handleDashboardRefresh}
            interactionLocked={stageOp !== null && stageOp.saveId === saveId}
            onStageStart={handleStageStart}
            onStageSettled={handleStageSettled}
            onStageMutationError={handleStageMutationError}
          />
        );
      case 'live': {
        const status = dashboard.data?.status ?? null;
        // MSS-143 correction: bare Live owns its routing through the shared
        // active-event resolver. The status `eventProgress.event` must not
        // pre-empt bare Live: on tiered saves the qualifier phase exposes only
        // `event='qualifier'` with phase-wide progress, and the legacy
        // `?event=qualifier` URL renders only the Superleague slice. After the
        // Superleague qualifier completes the active competition is a feeder
        // qualifier, so bare Live must resolve the active `?qualifier=` param
        // instead of showing the completed Superleague event.
        const isBareLiveEarly =
          route.event === null &&
          route.selection === null &&
          route.transition === null &&
          route.leagueId === null &&
          route.round === null &&
          route.qualifier === null;
        const liveEvent = route.event ?? (isBareLiveEarly ? null : (dashboard.data?.status?.eventProgress?.event ?? null));
        const eventSeason =
          route.eventSeason ?? status?.eventProgress?.sourceSeasonNumber ?? status?.sourceSeasonNumber ?? null;
        // MSS-053 postseason transition reveals are read-only presentations
        // over already-persisted movement/rebalance results. They take
        // precedence when the URL names one; reloading re-reads the persisted
        // result and never reruns the sporting mutation.
        if (route.transition && eventSeason !== null) {
          return (
            <TransitionRevealView
              key={`${route.transition}:${eventSeason}`}
              saveId={saveId}
              transition={route.transition}
              season={eventSeason}
            />
          );
        }
        // The Cup squad selection is an event of its own: shown when the URL
        // names it, or when it is the save's next lifecycle step.
        const pendingSelection = selectionForAction(status?.legalNextActions[0]);
        const liveSelection = route.selection ?? (route.event ? null : pendingSelection);
        if (liveSelection && eventSeason !== null) {
          return (
            <CupSelectionView
              key={`${liveSelection}:${eventSeason}`}
              saveId={saveId}
              selection={liveSelection}
              season={eventSeason}
              canAnnounce={pendingSelection === liveSelection && status?.sourceSeasonNumber === eventSeason}
              nextEvent={status?.eventProgress?.sourceSeasonNumber === eventSeason ? status.eventProgress.event : null}
              onPin={() => {
                navigate(livePath(saveId, { event: liveSelection, season: eventSeason }), { replace: true });
              }}
              onMutated={dashboard.refresh}
            />
          );
        }
        // MSS-109/110: Chaos Cup group navigation with scoped Live controls.
        // An explicit `?season=` replays that edition; absent follows the
        // latest edition from the read API. The URL keeps the selected
        // stage/group/round so refresh and Back/Forward replay the same group;
        // scoped POSTs re-read only the affected edition/group/round.
        if (route.event === 'chaos-cup') {
          const chaosSeason = route.eventSeason;
          return (
            <ChaosCupLiveView
              key={`chaos-cup:${chaosSeason ?? 'latest'}`}
              saveId={saveId}
              season={chaosSeason}
              urlStage={route.stage}
              urlGroup={route.group}
              urlRound={route.round}
              onSelect={(stage, group, round) => {
                navigate(chaosLivePath(saveId, chaosSeason, stage, group, round), { replace: true });
              }}
              onMutated={dashboard.refresh}
            />
          );
        }
        // MSS-143: bare Live (`/live` with no event/selection/transition or
        // league query) follows the authoritative active event instead of
        // stale Superleague league play. Explicit deep links above keep their
        // replay semantics; only the bare entry resolves here so refresh,
        // direct navigation, save/season switches and back/forward stay on
        // the current competition. The shared resolver (legal next action via
        // seasonFlow) owns Chaos, tiered feeder qualifiers and Dashboard-CTA
        // detection; bare Live never falls back to the status
        // `eventProgress.event` (which would pin tiered qualifiers to the
        // completed Superleague slice). Round-based Cup/Superleague-qualifier
        // events reuse the same LiveEventView below.
        const isBareLive = isBareLiveEarly;
        if (isBareLive && dashboard.data?.progress) {
          const progress = dashboard.data.progress;
          try {
            const target = resolveActiveLiveTarget(progress, status);
            if (target.kind === 'chaos') {
              return (
                <ChaosCupLiveView
                  key={`chaos-cup:active:${target.season}`}
                  saveId={saveId}
                  season={target.season}
                  urlStage={route.stage}
                  urlGroup={route.group}
                  urlRound={route.round}
                  onSelect={(stage, group, round) => {
                    navigate(chaosLivePath(saveId, target.season, stage, group, round), { replace: true });
                  }}
                  onMutated={dashboard.refresh}
                />
              );
            }
            if (target.kind === 'event') {
              return (
                <LiveEventView
                  key={`active:${target.event}:${target.season}`}
                  saveId={saveId}
                  event={target.event}
                  season={target.season}
                  progress={status?.eventProgress?.event === target.event ? status.eventProgress : null}
                  urlGroup={route.group}
                  urlRound={route.round}
                  urlTournamentPhase={route.tournamentPhase}
                  urlQualificationGroup={route.qualificationGroup}
                  onSelectRound={(group, round, stage) => {
                    navigate(
                      livePath(saveId, {
                        event: target.event,
                        season: target.season,
                        group,
                        round,
                        tournamentPhase: stage?.tournamentPhase ?? null,
                        qualificationGroup: stage?.qualificationGroup ?? null,
                      }),
                      { replace: true },
                    );
                  }}
                  onMutated={dashboard.refresh}
                />
              );
            }
            if (target.kind === 'qualifier') {
              // MSS-143 correction: tiered feeder qualifier is the active
              // competition once the Superleague qualifier completes.
              // Reuses the existing per-qualifier Live view with its own
              // 16-round reveal; never the stale Superleague LiveEventView.
              const canPlayQualifier =
                (status?.legalNextActions ?? []).includes('RunQualifier') &&
                (status?.eventProgress?.sourceSeasonNumber ?? status?.sourceSeasonNumber) === target.season;
              return (
                <LiveQualifierView
                  key={`qualifier:active:${target.qualifier}:${target.season}`}
                  saveId={saveId}
                  qualifier={target.qualifier}
                  season={target.season}
                  urlRound={route.round}
                  canPlay={canPlayQualifier}
                  onSelectRound={(round) => {
                    navigate(qualifierLivePath(saveId, target.qualifier, target.season, round), { replace: true });
                  }}
                  onMutated={dashboard.refresh}
                />
              );
            }
            if (target.kind === 'selection') {
              return (
                <CupSelectionView
                  key={`active:${target.selection}:${target.season}`}
                  saveId={saveId}
                  selection={target.selection}
                  season={target.season}
                  canAnnounce={pendingSelection === target.selection && status?.sourceSeasonNumber === target.season}
                  nextEvent={status?.eventProgress?.sourceSeasonNumber === target.season ? status.eventProgress.event : null}
                  onPin={() => {
                    navigate(livePath(saveId, { event: target.selection, season: target.season }), { replace: true });
                  }}
                  onMutated={dashboard.refresh}
                />
              );
            }
            if (target.kind === 'dashboard') {
              const flowNext = seasonFlow(progress, status).next;
              const headline =
                flowNext?.kind === 'event' ? flowNext.label : 'No playable live event';
              const explanation =
                flowNext?.kind === 'event'
                  ? flowNext.explanation
                  : 'The next season step must be performed on the Dashboard before Live has a playable event.';
              return (
                <LiveDashboardCta
                  key={`dashboard-cta:${target.season ?? 'unknown'}:${target.action ?? 'none'}`}
                  saveId={saveId}
                  season={target.season}
                  action={target.action}
                  headline={headline}
                  explanation={explanation}
                />
              );
            }
          } catch {
            // Resolver is pure but defensive: fall through to league LivePage
            // rather than crashing bare Live on unexpected lifecycle shapes.
          }
        }
        if (liveEvent && eventSeason !== null) {
          // MSS-069: individual feeder qualifiers keep distinct Live URLs
          // (`?event=qualifier&qualifier=f1f2-white&season=N&round=M`) with
          // their own 16-round reveal. The Superleague event keeps the legacy
          // `?event=qualifier` URL and its 32-athlete reveal (now 16 / 16).
          if (liveEvent === 'qualifier' && route.qualifier && route.qualifier !== 'superleague') {
            const canPlay =
              (status?.legalNextActions ?? []).includes('RunQualifier') &&
              status?.eventProgress?.sourceSeasonNumber === eventSeason;
            return (
              <LiveQualifierView
                key={`qualifier:${route.qualifier}:${eventSeason}`}
                saveId={saveId}
                qualifier={route.qualifier}
                season={eventSeason}
                urlRound={route.round}
                canPlay={canPlay}
                onSelectRound={(round) => {
                  navigate(
                    livePath(saveId, {
                      event: liveEvent,
                      qualifier: route.qualifier,
                      season: eventSeason,
                      round,
                    }),
                    { replace: true },
                  );
                }}
                onMutated={dashboard.refresh}
              />
            );
          }
          return (
            <LiveEventView
              key={`${liveEvent}:${eventSeason}`}
              saveId={saveId}
              event={liveEvent}
              season={eventSeason}
              progress={status?.eventProgress?.event === liveEvent ? status.eventProgress : null}
              urlGroup={route.group}
              urlRound={route.round}
              urlTournamentPhase={route.tournamentPhase}
              urlQualificationGroup={route.qualificationGroup}
              onSelectRound={(group, round, stage) => {
                navigate(
                  livePath(saveId, {
                    event: liveEvent,
                    season: eventSeason,
                    group,
                    round,
                    tournamentPhase: stage?.tournamentPhase ?? null,
                    qualificationGroup: stage?.qualificationGroup ?? null,
                  }),
                  { replace: true },
                );
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
            urlEvent={route.event}
            urlGroup={route.group}
            urlTournamentPhase={route.tournamentPhase}
            urlQualificationGroup={route.qualificationGroup}
            onHistoryChange={(selection) => {
              navigate(
                historyPath(saveId, {
                  season: selection.season,
                  competition: selection.competition,
                  stage: selection.stage,
                  round: selection.round,
                  event: selection.event,
                  group: selection.group,
                  tournamentPhase: selection.tournamentPhase,
                  qualificationGroup: selection.qualificationGroup,
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
            onStandingsChange={(selection, options) => {
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
                options?.replace ? { replace: true } : undefined,
              );
            }}
          />
        );
      case 'records':
        return <RecordsPage key={`${saveId}/records`} saveId={saveId} urlView={route.view} />;
      case 'reports':
        return <ReportsPage key={saveId} saveId={saveId} />;
      case 'leagues':
        if (route.leagueKey) {
          return <LeagueProfilePage key={`${saveId}/${route.leagueKey}`} saveId={saveId} leagueKey={route.leagueKey} />;
        }
        return <LeaguesOverviewPage key={saveId} saveId={saveId} />;
      case 'qualifiers':
        if (route.boundary) {
          return (
            <QualifierDetailPage
              saveId={saveId}
              boundary={route.boundary}
              color={route.color}
              urlSeason={route.season}
              onMutated={dashboard.refresh}
            />
          );
        }
        return (
          <QualifiersPage
            saveId={saveId}
            urlSeason={route.season}
            onQualifiersChange={(selection) => {
              navigate(qualifiersPath(saveId, { season: selection.season }));
            }}
            onMutated={dashboard.refresh}
          />
        );
      case 'cups':
        if (route.view.kind === 'edition') {
          return <CupEditionPage saveId={saveId} cup={route.view.cup} season={route.view.season} />;
        }
        if (route.view.kind === 'team') {
          return <CupTeamPage saveId={saveId} cup={route.view.cup} teamKey={route.view.teamKey} />;
        }
        return <CupsHubPage saveId={saveId} />;
      case 'athletes':
        return (
          <AthleteSearchPage
            key={saveId}
            saveId={saveId}
            urlSearch={route.search}
            onSearchChange={(search) => {
              navigate(athletesPath(saveId, search), { replace: true });
            }}
          />
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
    <>
      <AppShell
        view={viewForRoute(route)}
        saveId={routeSaveId ?? lastSelected}
        saveName={dashboard.data?.detail.name ?? selectedSave?.name ?? null}
        seasonLabel={seasonLabel}
        stageLabel={stageLabel}
        catalog={catalogStatus(catalog.stats, catalog.loading)}
        locked={stageLocked}
        lockLabel="Stage completion in progress — navigation is paused."
        liveHref={liveHref}
      >
        <CatalogBanner stats={catalog.stats} loading={catalog.loading} onImported={catalog.refresh} />
        {renderBody()}
      </AppShell>
      {stageOp ? (
        <StageCompletionBarrier
          operation={stageOp}
          refreshError={stageRefreshError}
          onRetryRefresh={handleRetryStageRefresh}
        />
      ) : null}
    </>
  );
}
