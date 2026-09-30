import { useState } from 'react';
import { apiErrorMessage } from '../../shared/api/http';
import { Notice } from '../../shared/ui/Notice';
import { Link } from '../routing/router';
import { cupsPath, livePath, standingsPath } from '../routing/routes';
import { advanceToNextEvent, type SeasonProgress, type SeasonStatus } from './dashboardApi';
import { executedSummary, seasonFlow, type SummaryTarget } from './seasonFlowSteps';

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
  onAdvanced,
}: {
  saveId: string;
  progress: SeasonProgress;
  status: SeasonStatus | null;
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
            <Link to={livePath(saveId)} className="primary-button">
              Go to Live
            </Link>
          ) : next.liveEvent ? (
            <div className="live-buttons">
              <Link to={livePath(saveId, { event: next.liveEvent, season: flow.seasonNumber })} className="primary-button">
                Play on Live
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
                {running ? 'Running…' : 'Run all rounds'}
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
                void runNext();
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
