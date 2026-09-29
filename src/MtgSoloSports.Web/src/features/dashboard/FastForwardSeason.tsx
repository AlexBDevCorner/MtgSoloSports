import { useState } from 'react';
import { Notice } from '../../shared/ui/Notice';
import { ApiError, apiErrorMessage } from '../../shared/api/http';
import { Link } from '../routing/router';
import { cupsPath, historyPath, standingsPath } from '../routing/routes';
import {
  completeSeason,
  type CompleteSeasonResult,
  type SeasonProgress,
  type SeasonStatus,
} from './dashboardApi';
import {
  fastForwardAvailability,
  fastForwardConfirmationText,
  postFastForwardNextStep,
  summarizeCompleteSeason,
} from './fastForwardSeason';

type Phase = 'idle' | 'confirming' | 'running' | 'success' | 'error';

/**
 * MSS-042 one-click "Fast-forward league season" shortcut, rendered
 * prominently inside the Dashboard "Available simulation" card.
 *
 * Uses the route-scoped save ID and calls the existing
 * `POST /api/saves/{saveId}/seasons/complete-season` bulk operation exactly
 * once per confirmation. The backend completes the remaining league stages
 * for every active league with the same kernel/RNG as manual play, persists
 * stage boundaries, and stops after Stage 32 results — before movement,
 * qualifiers, Cup selection/events and the next-season transition, which stay
 * on the normal one-event-at-a-time lifecycle controls.
 */
export function FastForwardSeason({
  saveId,
  progress,
  status,
  onCompleted,
}: {
  saveId: string;
  progress: SeasonProgress;
  status: SeasonStatus | null;
  onCompleted: () => void;
}) {
  const [phase, setPhase] = useState<Phase>('idle');
  const [error, setError] = useState<string | null>(null);
  const [wasConflict, setWasConflict] = useState(false);
  const [result, setResult] = useState<CompleteSeasonResult | null>(null);

  const availability = fastForwardAvailability(progress, status);
  const running = phase === 'running';

  async function handleConfirm(): Promise<void> {
    if (running) {
      return;
    }
    setPhase('running');
    setError(null);
    setWasConflict(false);
    try {
      const completed = await completeSeason(saveId);
      setResult(completed);
      setPhase('success');
      onCompleted();
    } catch (failure) {
      setError(apiErrorMessage(failure));
      setWasConflict(failure instanceof ApiError && failure.status === 409);
      setPhase('error');
    }
  }

  if (phase === 'success' && result) {
    return (
      <section aria-label="Fast-forward league season — completed">
        <Notice tone="info" title="League season complete">
          <p>{summarizeCompleteSeason(result)}</p>
          <p>{postFastForwardNextStep(result)}</p>
          <p className="muted small">
            No postseason action has run: movement, qualifier, Cup selection and Cup events are
            still pending on the normal lifecycle controls, one event at a time.
          </p>
        </Notice>
        <div className="live-buttons" role="group" aria-label="Postseason next steps">
          <Link to={standingsPath(saveId)} className="primary-button">
            Inspect final tables
          </Link>
          <Link to={historyPath(saveId)} className="ghost-button">
            Replay history
          </Link>
          <Link to={cupsPath(saveId)} className="ghost-button">
            Inspect Cups
          </Link>
          <button type="button" className="ghost-button" onClick={onCompleted}>
            Refresh status
          </button>
        </div>
      </section>
    );
  }

  if (phase === 'error') {
    return (
      <section aria-label="Fast-forward league season — failed">
        <Notice tone="error" title="Fast-forward did not complete">
          <p>{error ?? 'The bulk season completion failed.'}</p>
          {wasConflict ? (
            <p className="muted small">
              The backend reported a conflict: another simulation may be active for this save, or
              the current phase no longer allows bulk stage completion. Refresh authoritative state
              before retrying.
            </p>
          ) : (
            <p className="muted small">
              Nothing is claimed as completed. Dismissing this message or aborting the request does
              not reverse stages the backend already committed; refresh to recheck authoritative
              server state.
            </p>
          )}
          <div className="live-buttons" role="group" aria-label="Fast-forward recovery">
            <button type="button" className="ghost-button" onClick={onCompleted}>
              Refresh state
            </button>
            {availability.available ? (
              <button
                type="button"
                className="primary-button"
                disabled={running}
                onClick={() => {
                  setPhase('confirming');
                }}
              >
                Retry fast-forward
              </button>
            ) : null}
            <button
              type="button"
              className="ghost-button"
              onClick={() => {
                setError(null);
                setPhase('idle');
              }}
            >
              Dismiss
            </button>
          </div>
          {!availability.available ? <p className="muted small">{availability.reason}</p> : null}
        </Notice>
      </section>
    );
  }

  if (phase === 'confirming' && availability.available) {
    return (
      <section aria-label="Fast-forward league season — confirm">
        <p>
          <strong>{fastForwardConfirmationText(progress, availability.remainingGlobalStages)}</strong>
        </p>
        <p className="muted small">
          One backend bulk operation (POST /api/saves/{saveId}/seasons/complete-season) advances
          every active league — it does not replay rounds one by one and never runs postseason. If
          you leave or reload mid-run, already committed stages stay committed; refresh to recheck
          authoritative progress.
        </p>
        <div className="live-buttons" role="group" aria-label="Fast-forward confirmation">
          <button
            type="button"
            className="primary-button"
            disabled={running}
            onClick={() => {
              void handleConfirm();
            }}
          >
            Confirm fast-forward
          </button>
          <button
            type="button"
            className="ghost-button"
            disabled={running}
            onClick={() => {
              setPhase('idle');
            }}
          >
            Cancel
          </button>
        </div>
      </section>
    );
  }

  if (running) {
    return (
      <section aria-label="Fast-forward league season — running">
        <p role="status" aria-live="polite" aria-busy="true">
          Fast-forwarding Season {progress.seasonNumber} from global stage {progress.globalStage}…
          All active leagues advance in one backend operation.
        </p>
        <progress aria-label="Fast-forward league season in progress" />
        <p className="muted small">
          The endpoint returns only the final counts and cursors, so no per-stage progress is
          shown. Do not submit again; duplicate submissions are blocked while this runs. If the
          request is interrupted, refresh to recheck authoritative server state.
        </p>
      </section>
    );
  }

  return (
    <section aria-label="Fast-forward league season">
      <p className="muted">
        Completes the <strong>remaining league stages for every league</strong> in one step and{' '}
        <strong>stops before movement, qualifiers and Cups</strong>, so those events can be
        inspected separately.
      </p>
      {availability.available ? (
        <>
          <button
            type="button"
            className="primary-button"
            onClick={() => {
              setPhase('confirming');
            }}
          >
            Fast-forward league season
          </button>
          <p className="muted small">
            Season {progress.seasonNumber} · {availability.remainingGlobalStages} global stage
            {availability.remainingGlobalStages === 1 ? '' : 's'} remaining across{' '}
            {progress.leagues.length} active league
            {progress.leagues.length === 1 ? '' : 's'} · saved results cannot be undone through the
            UI.
          </p>
        </>
      ) : (
        <>
          <button type="button" className="primary-button" disabled title={availability.reason}>
            Fast-forward league season
          </button>
          <p className="muted small">{availability.reason}</p>
        </>
      )}
    </section>
  );
}
