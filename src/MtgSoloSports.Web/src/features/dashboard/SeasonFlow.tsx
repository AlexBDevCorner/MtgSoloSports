import { useRef, useState } from 'react';
import { apiErrorMessage } from '../../shared/api/http';
import { Loading, Notice } from '../../shared/ui/Notice';
import { Link, navigate } from '../routing/router';
import { cupsPath, livePath, qualifiersPath, standingsPath } from '../routing/routes';
import { transitionForAction } from '../events/eventModel';
import { cupLabel } from '../../shared/displayLabels';
import { advanceToNextEvent, type SeasonProgress, type SeasonStatus } from './dashboardApi';
import { executedSummary, isTieredProgress, seasonFlow, type SummaryTarget } from './seasonFlowSteps';

const TARGET_LINKS: Record<SummaryTarget, { label: string; path: (saveId: string) => string }> = {
  standings: { label: 'View standings', path: (saveId) => standingsPath(saveId) },
  cups: { label: 'View Cups', path: (saveId) => cupsPath(saveId) },
  live: { label: 'Go to Live', path: (saveId) => livePath(saveId) },
};

/**
 * Season stepper plus the single "next event" control. Each click runs exactly
 * one backend lifecycle step via `advance-next-event`; results are persisted
 * by the backend and the dashboard re-reads them afterwards.
 */
export function SeasonFlow({
  saveId,
  progress,
  status,
  statusError,
  refreshError,
  onAdvanced,
  onLeagueCompleted,
  transitionPending,
  interactionLocked,
  onStageStart,
  onStageMutationError,
}: {
  saveId: string;
  progress: SeasonProgress;
  status: SeasonStatus | null;
  /**
   * MSS-070: the season-status fetch failure from `useDashboard`, if any.
   * While set, the panel must show a recoverable error with retry and must
   * never claim no actions remain.
   */
  statusError?: string | null;
  /**
   * MSS-074: top-level dashboard refresh failure. `useDashboard` retains the
   * stale pre-completion snapshot on such an error, so the pending transition
   * must surface this retry instead of restoring stale League-play controls.
   */
  refreshError?: string | null;
  onAdvanced: () => void;
  /**
   * MSS-074: called when a manual `Complete stage` commit finishes the league
   * season. The Dashboard sets its pending-transition guard and refreshes
   * authoritative status, so stale enabled League-play controls never return
   * while the refresh is pending.
   */
  onLeagueCompleted?: () => void;
  /**
   * MSS-074: true while a fast-forward commit has been persisted and the
   * authoritative postseason status refresh is still pending. While set, the
   * panel shows a stable loading state instead of stale League-play content,
   * so the removed intermediate completed-league screen never flashes.
   */
  transitionPending?: boolean;
  /**
   * MSS-124: true while a Dashboard stage completion (mutation + refresh)
   * is pending. All mutation buttons stay disabled and Live/navigation links
   * render inert so pointer and keyboard input cannot reach them; the App
   * shell and router enforce the same guard for background navigation.
   */
  interactionLocked?: boolean;
  /**
   * MSS-124: notified synchronously when a stage completion starts, before
   * the POST resolves, so the app can show the busy message immediately and
   * keep it through the authoritative refresh.
   */
  onStageStart?: (info: { stage: number; leagueCount: number }) => void;
  /**
   * MSS-124: notified when the stage mutation itself fails. The app keeps
   * the guard for uncertain (possibly committed) failures until authoritative
   * state is rechecked, and clears it for certain failures so the local
   * recoverable error becomes actionable.
   */
  onStageMutationError?: (error: unknown) => void;
}) {
  const [running, setRunning] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [lastDone, setLastDone] = useState<{ text: string; target: SummaryTarget } | null>(null);
  // MSS-123: Chaos Cup full-tournament fast-forward needs an explicit
  // confirmation; the request button never calls the mutation directly.
  const [confirmChaosCup, setConfirmChaosCup] = useState(false);
  // Synchronous in-flight guard so a rapid double-click cannot issue two
  // `advance-next-event` POSTs before the `running` state re-renders.
  const runningRef = useRef(false);
  const locked = interactionLocked === true;

  if (transitionPending) {
    const pendingError = statusError ?? refreshError ?? null;
    return (
      <div className="season-flow">
        <Loading label="Loading postseason status…" />
        {pendingError ? (
          <Notice tone="error" title="Postseason refresh failed">
            <p>{pendingError}</p>
            <p>
              <button type="button" className="ghost-button" disabled={running || locked} onClick={onAdvanced}>
                Retry
              </button>
            </p>
            <p className="muted small">
              League results already persisted; retry loads the authoritative postseason step.
            </p>
          </Notice>
        ) : null}
      </div>
    );
  }

  const flow = seasonFlow(progress, status);
  const next = flow.next;
  const controlsDisabled = running || locked;

  async function runNext(): Promise<void> {
    if (running || runningRef.current || locked) {
      return;
    }
    runningRef.current = true;
    setRunning(true);
    setError(null);
    // MSS-124: announce stage completion synchronously so the busy message
    // appears immediately and survives through the authoritative refresh.
    // Only league-play stage completion owns this guard; other postseason
    // events keep their existing single-flight running guard.
    if (next?.kind === 'live') {
      onStageStart?.({ stage: progress.globalStage, leagueCount: progress.leagues.length });
    }
    try {
      const result = await advanceToNextEvent(saveId);
      setConfirmChaosCup(false);
      setLastDone(
        executedSummary(
          result.executedAction,
          result.nextSeasonNumber ?? result.currentSeasonNumber,
          isTieredProgress(progress),
          status?.qualifierProgress?.totalEvents ?? status?.eventProgress?.totalEvents ?? result.qualifierProgress?.totalEvents ?? result.eventProgress?.totalEvents ?? null,
        ),
      );
      // MSS-074: a manual commit of the final league stage must enter the same
      // pending-transition loading state as fast-forward until the authoritative
      // refresh lands; otherwise stale enabled League-play controls flash.
      const leagueJustCompleted =
        result.executedAction === 'CompleteNextGlobalStage' &&
        (result.isCurrentSeasonComplete || result.seasonComplete);
      if (leagueJustCompleted && onLeagueCompleted) {
        onLeagueCompleted();
      } else {
        onAdvanced();
      }
    } catch (failure) {
      setError(apiErrorMessage(failure));
      // MSS-124: let the app decide whether the guard must survive until a
      // refresh rechecks authoritative state (uncertain outcome) or can clear
      // immediately (certain failure with a local retry).
      if (next?.kind === 'live') {
        onStageMutationError?.(failure);
      }
    } finally {
      runningRef.current = false;
      setRunning(false);
    }
  }

  /**
   * MSS-053 transition steps (promotion/relegation, feeder rebalance,
   * inaugural formation). The backend persists the authoritative result
   * exactly once; the UI then navigates to the dedicated Live reveal for the
   * season that was just persisted. The reveal page re-reads that persisted
   * result, so refresh and Back/Forward never rerun the sporting action.
   */
  async function runNextAndReveal(): Promise<void> {
    if (running || runningRef.current || locked || next?.kind !== 'event' || !next.liveTransition) {
      return;
    }
    runningRef.current = true;
    setRunning(true);
    setError(null);
    try {
      const result = await advanceToNextEvent(saveId);
      setLastDone(
        executedSummary(
          result.executedAction,
          result.nextSeasonNumber ?? result.currentSeasonNumber,
          isTieredProgress(progress),
          status?.qualifierProgress?.totalEvents ?? status?.eventProgress?.totalEvents ?? result.qualifierProgress?.totalEvents ?? result.eventProgress?.totalEvents ?? null,
        ),
      );
      onAdvanced();
      const transition = transitionForAction(result.executedAction) ?? next.liveTransition;
      const fromSeason = result.sourceSeasonNumber ?? flow.seasonNumber;
      navigate(livePath(saveId, { transition, season: fromSeason }));
    } catch (failure) {
      setError(apiErrorMessage(failure));
    } finally {
      runningRef.current = false;
      setRunning(false);
    }
  }

  // MSS-123: the Dashboard fast-forward for `RunChaosCup` completes all six
  // remaining stages at once. It needs its own explicit label plus a second
  // confirmation click; the request control never calls the mutation itself.
  const isChaosCupNext =
    next?.kind === 'event' && (next.action === 'RunChaosCup' || next.liveEvent === 'chaos-cup');

  function handleRequestChaosCup(): void {
    if (running || locked || runningRef.current || !isChaosCupNext) {
      return;
    }
    setConfirmChaosCup(true);
  }

  function handleConfirmChaosCup(): void {
    if (!confirmChaosCup || !isChaosCupNext) {
      return;
    }
    void runNext();
  }

  const doneLink = lastDone ? TARGET_LINKS[lastDone.target] : null;

  return (
    <div className="season-flow">
      <ol className="flow-steps" aria-label={`Season ${flow.seasonNumber} progress`}>
        {flow.steps.map((step, index) => (
          <li
            key={step.key}
            className={`flow-step is-${step.state}`}
            aria-current={step.state === 'current' ? 'step' : undefined}
          >
            <span className="flow-mark" aria-hidden="true">
              {step.state === 'done' ? '✓' : index + 1}
            </span>
            <span className="flow-label">
              {step.label}
              {step.detail ? <span className="flow-detail">{step.detail}</span> : null}
            </span>
          </li>
        ))}
      </ol>

      {next ? (
        <div className="flow-next">
          <div className="flow-next-text">
            <p className="next-headline">Next: {next.label}</p>
            <p className="muted">{next.explanation}</p>
          </div>
          {next.kind === 'live' ? (
            <div className="live-buttons">
              <button
                type="button"
                className="primary-button"
                disabled={controlsDisabled}
                aria-busy={running}
                title={`Completes stage ${next.globalStage} for all ${next.leagueCount} leagues in one backend operation.`}
                onClick={() => {
                  void runNext();
                }}
              >
                {running || locked ? 'Completing…' : `Complete stage ${next.globalStage} for all ${next.leagueCount} leagues`}
              </button>
              {locked ? (
                <span className="ghost-button" aria-disabled="true" title="Stage completion in progress — navigation is paused.">
                  Go to Live
                </span>
              ) : (
                <Link to={livePath(saveId)} className="ghost-button">
                  Go to Live
                </Link>
              )}
            </div>
          ) : next.liveEvent ? (
            isChaosCupNext ? (
              <>
                <div className="live-buttons">
                  {locked ? (
                    <span className="primary-button" aria-disabled="true">
                      Play on Live
                    </span>
                  ) : (
                    <Link to={livePath(saveId, { event: next.liveEvent, season: flow.seasonNumber })} className="primary-button">
                      Play on Live
                    </Link>
                  )}
                  {confirmChaosCup ? null : (
                    <button
                      type="button"
                      className="ghost-button"
                      disabled={controlsDisabled}
                      aria-busy={running}
                      title="Tournament-wide fast-forward: plays every remaining group and the Final to the official places."
                      onClick={handleRequestChaosCup}
                    >
                      {running ? 'Running…' : 'Finish entire Chaos Cup'}
                    </button>
                  )}
                </div>
                {confirmChaosCup ? (
                  <Notice tone="error" title="Finish the entire Chaos Cup?">
                    <p>
                      This plays every remaining group and the Final to the official places. To stop at
                      the current group or stage, use the scoped Live controls instead.
                    </p>
                    <p className="live-buttons">
                      <button
                        type="button"
                        className="ghost-button"
                        disabled={controlsDisabled}
                        aria-busy={running}
                        onClick={handleConfirmChaosCup}
                      >
                        {running ? 'Finishing…' : 'Yes, finish entire Chaos Cup'}
                      </button>
                      <button
                        type="button"
                        className="ghost-button"
                        disabled={controlsDisabled}
                        onClick={() => setConfirmChaosCup(false)}
                      >
                        Keep group/stage scope
                      </button>
                    </p>
                  </Notice>
                ) : null}
              </>
            ) : (
              <div className="live-buttons">
                {next.liveEvent === 'qualifier' && isTieredProgress(progress) ? (
                  locked ? (
                    <span className="primary-button" aria-disabled="true">
                      {(status?.qualifierProgress?.totalEvents ?? status?.eventProgress?.totalEvents)
                        ? `Open ${status?.qualifierProgress?.totalEvents ?? status?.eventProgress?.totalEvents} qualifiers`
                        : 'Open qualifiers'}
                    </span>
                  ) : (
                    <Link
                      to={qualifiersPath(saveId, { season: flow.seasonNumber })}
                      className="primary-button"
                    >
                      {(status?.qualifierProgress?.totalEvents ?? status?.eventProgress?.totalEvents)
                        ? `Open ${status?.qualifierProgress?.totalEvents ?? status?.eventProgress?.totalEvents} qualifiers`
                        : 'Open qualifiers'}
                    </Link>
                  )
                ) : locked ? (
                  <span className="primary-button" aria-disabled="true">
                    Play on Live
                  </span>
                ) : (
                  <Link to={livePath(saveId, { event: next.liveEvent, season: flow.seasonNumber })} className="primary-button">
                    Play on Live
                  </Link>
                )}
                <button
                  type="button"
                  className="ghost-button"
                  disabled={controlsDisabled}
                  aria-busy={running}
                  onClick={() => {
                    void runNext();
                  }}
                >
                  {running ? 'Running…' : 'Run all rounds'}
                </button>
              </div>
            )
          ) : next.liveSelection ? (
            <div className="live-buttons">
              {locked ? (
                <span className="primary-button" aria-disabled="true">
                  Announce on Live
                </span>
              ) : (
                <Link
                  to={livePath(saveId, { event: next.liveSelection, season: flow.seasonNumber })}
                  className="primary-button"
                >
                  Announce on Live
                </Link>
              )}
              <button
                type="button"
                className="ghost-button"
                disabled={controlsDisabled}
                aria-busy={running}
                onClick={() => {
                  void runNext();
                }}
              >
                {running ? 'Selecting…' : 'Select now'}
              </button>
            </div>
          ) : next.liveTransition ? (
            <div className="live-buttons">
              <button
                type="button"
                className="primary-button"
                disabled={controlsDisabled}
                aria-busy={running}
                onClick={() => {
                  void runNextAndReveal();
                }}
              >
                {running ? 'Resolving…' : `${next.label} on Live`}
              </button>
            </div>
          ) : (
            <button
              type="button"
              className="primary-button"
              disabled={controlsDisabled}
              aria-busy={running}
              onClick={() => {
                void runNext();
              }}
            >
              {running ? 'Running…' : next.label}
            </button>
          )}
        </div>
      ) : statusError ? (
        <Notice tone="error" title="Season status unavailable">
          <p>{statusError}</p>
          <p>
            <button
              type="button"
              className="ghost-button"
              disabled={controlsDisabled}
              onClick={() => {
                onAdvanced();
              }}
            >
              Retry
            </button>
          </p>
        </Notice>
      ) : !status ? (
        <Notice tone="warn" title="Season status unavailable">
          <p>
            The next postseason step could not be loaded. A completed league season is not the same as a
            completed postseason — refresh before assuming nothing remains.
          </p>
          <p>
            <button
              type="button"
              className="ghost-button"
              disabled={controlsDisabled}
              onClick={() => {
                onAdvanced();
              }}
            >
              Refresh
            </button>
          </p>
        </Notice>
      ) : status.legalNextActions.length === 0 && !status.cupComplete && !status.readyToStartNextSeason ? (
        <Notice tone="warn" title="Postseason incomplete">
          <p>
            The backend returned no next step, but the {cupLabel(status.expectedCup)} is not complete. Refresh to
            reload the authoritative lifecycle — this panel never invents its own next action.
          </p>
          <p>
            <button
              type="button"
              className="ghost-button"
              disabled={controlsDisabled}
              onClick={() => {
                onAdvanced();
              }}
            >
              Refresh
            </button>
          </p>
        </Notice>
      ) : (
        <p className="muted">Nothing left to run for this season.</p>
      )}

      {lastDone && doneLink && !error ? (
        <p className="flow-done" role="status">
          <span aria-hidden="true">✓ </span>Done: {lastDone.text}{' '}
          {locked ? (
            <span aria-disabled="true">{doneLink.label}</span>
          ) : (
            <Link to={doneLink.path(saveId)}>{doneLink.label}</Link>
          )}
        </p>
      ) : null}

      {error ? (
        <Notice tone="error" title="The step did not complete">
          <p>{error}</p>
            <p>
              <button
                type="button"
                className="ghost-button"
                disabled={controlsDisabled}
                onClick={() => {
                  if (next?.kind === 'event' && next.liveTransition) {
                    void runNextAndReveal();
                  } else {
                    void runNext();
                  }
                }}
              >
                Try again
              </button>
            </p>
            {locked ? (
              <p className="muted small">
                Stage completion in progress — navigation and other simulation controls stay paused
                until the authoritative refresh lands.
              </p>
            ) : (
              <p className="muted small">
                If this request was interrupted, refresh the dashboard to recheck authoritative state
                before retrying; an interrupted stage may already be persisted.
              </p>
            )}
        </Notice>
      ) : null}

      {next?.kind === 'event' ? (
        <p className="muted small">{"Each step is saved and can't be undone."}</p>
      ) : null}
    </div>
  );
}
