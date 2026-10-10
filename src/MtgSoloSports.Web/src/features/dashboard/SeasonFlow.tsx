import { useState } from 'react';
import { apiErrorMessage } from '../../shared/api/http';
import { Notice } from '../../shared/ui/Notice';
import { Link, navigate } from '../routing/router';
import { cupsPath, livePath, qualifiersPath, standingsPath } from '../routing/routes';
import { transitionForAction } from '../events/eventModel';
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
  onAdvanced,
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
  onAdvanced: () => void;
}) {
  const [running, setRunning] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [lastDone, setLastDone] = useState<{ text: string; target: SummaryTarget } | null>(null);

  const flow = seasonFlow(progress, status);
  const next = flow.next;

  async function runNext(): Promise<void> {
    if (running) {
      return;
    }
    setRunning(true);
    setError(null);
    try {
      const result = await advanceToNextEvent(saveId);
      setLastDone(executedSummary(result.executedAction, result.nextSeasonNumber ?? result.currentSeasonNumber));
      onAdvanced();
    } catch (failure) {
      setError(apiErrorMessage(failure));
    } finally {
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
    if (running || next?.kind !== 'event' || !next.liveTransition) {
      return;
    }
    setRunning(true);
    setError(null);
    try {
      const result = await advanceToNextEvent(saveId);
      setLastDone(executedSummary(result.executedAction, result.nextSeasonNumber ?? result.currentSeasonNumber));
      onAdvanced();
      const transition = transitionForAction(result.executedAction) ?? next.liveTransition;
      const fromSeason = result.sourceSeasonNumber ?? flow.seasonNumber;
      navigate(livePath(saveId, { transition, season: fromSeason }));
    } catch (failure) {
      setError(apiErrorMessage(failure));
    } finally {
      setRunning(false);
    }
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
                disabled={running}
                aria-busy={running}
                title={`Completes stage ${next.globalStage} for all ${next.leagueCount} leagues in one backend operation.`}
                onClick={() => {
                  void runNext();
                }}
              >
                {running ? 'Completing…' : `Complete stage ${next.globalStage} for all ${next.leagueCount} leagues`}
              </button>
              <Link to={livePath(saveId)} className="ghost-button">
                Go to Live
              </Link>
            </div>
          ) : next.liveEvent ? (
            <div className="live-buttons">
              {next.liveEvent === 'qualifier' && isTieredProgress(progress) ? (
                <Link
                  to={qualifiersPath(saveId, { season: flow.seasonNumber })}
                  className="primary-button"
                >
                  Open 17 qualifiers
                </Link>
              ) : (
                <Link to={livePath(saveId, { event: next.liveEvent, season: flow.seasonNumber })} className="primary-button">
                  Play on Live
                </Link>
              )}
              <button
                type="button"
                className="ghost-button"
                disabled={running}
                aria-busy={running}
                onClick={() => {
                  void runNext();
                }}
              >
                {running ? 'Running…' : 'Run all rounds'}
              </button>
            </div>
          ) : next.liveSelection ? (
            <div className="live-buttons">
              <Link
                to={livePath(saveId, { event: next.liveSelection, season: flow.seasonNumber })}
                className="primary-button"
              >
                Announce on Live
              </Link>
              <button
                type="button"
                className="ghost-button"
                disabled={running}
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
                disabled={running}
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
              disabled={running}
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
              disabled={running}
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
              disabled={running}
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
            The backend returned no next step, but the {status.expectedCup} is not complete. Refresh to
            reload the authoritative lifecycle — this panel never invents its own next action.
          </p>
          <p>
            <button
              type="button"
              className="ghost-button"
              disabled={running}
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
          <Link to={doneLink.path(saveId)}>{doneLink.label}</Link>
        </p>
      ) : null}

      {error ? (
        <Notice tone="error" title="The step did not complete">
          <p>{error}</p>
            <p>
              <button
                type="button"
                className="ghost-button"
                disabled={running}
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
        </Notice>
      ) : null}

      {next?.kind === 'event' ? (
        <p className="muted small">{"Each step is saved and can't be undone."}</p>
      ) : null}
    </div>
  );
}
