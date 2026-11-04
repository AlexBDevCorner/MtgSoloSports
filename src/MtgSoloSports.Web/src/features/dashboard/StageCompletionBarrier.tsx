import { useEffect, useRef } from 'react';
import { Notice } from '../../shared/ui/Notice';
import { stageCompletionBusyLabel, type StageCompletionInfo } from './stageCompletionGuard';

/**
 * MSS-124 interaction guard for Dashboard stage completion. Rendered above
 * the (inert) shell + dashboard while the mutation and its authoritative
 * refresh are pending. Focus moves into the dialog on open and returns to
 * the previously focused control on close; the scrim blocks pointer input
 * while `inert` + disabled controls + the router blocker cover keyboard
 * and programmatic navigation.
 */
export function StageCompletionBarrier({
  operation,
  refreshError,
  onRetryRefresh,
}: {
  operation: StageCompletionInfo;
  refreshError: string | null;
  onRetryRefresh: () => void;
}) {
  const titleRef = useRef<HTMLHeadingElement | null>(null);
  const previousFocusRef = useRef<HTMLElement | null>(null);
  const busyLabel = stageCompletionBusyLabel(operation);

  useEffect(() => {
    previousFocusRef.current =
      typeof document !== 'undefined' && document.activeElement instanceof HTMLElement
        ? document.activeElement
        : null;
    const title = titleRef.current as unknown as { focus?: () => void } | null;
    title?.focus?.();
    return () => {
      previousFocusRef.current?.focus();
    };
  }, []);

  return (
    <div className="stage-guard-scrim" data-testid="stage-completion-guard">
      <div
        className="stage-guard-dialog"
        role="alertdialog"
        aria-modal="true"
        aria-labelledby="stage-guard-title"
        aria-describedby="stage-guard-desc"
      >
        <h2 id="stage-guard-title" tabIndex={-1} ref={titleRef} className="stage-guard-title">
          {busyLabel}
        </h2>
        {refreshError ? (
          <Notice tone="error" title="Refresh failed after the stage completed">
            <p>{refreshError}</p>
            <p className="muted small">
              League results already persisted; retry loads the authoritative step. It never resubmits
              the completed stage.
            </p>
            <p>
              <button type="button" className="primary-button" onClick={onRetryRefresh}>
                Retry refresh
              </button>
            </p>
            <p className="muted small">
              Reloading loads the same authoritative state; closing does not cancel server work.
            </p>
          </Notice>
        ) : (
          <>
            <div className="loading" role="status" aria-live="polite" aria-busy="true">
              <span className="spinner" aria-hidden="true" />
              <span>Calculation running — refreshing the authoritative result next.</span>
            </div>
            <p id="stage-guard-desc" className="muted small">
              One backend operation for this save. Do not submit again; duplicate submissions stay
              blocked while this runs. Already committed stages stay committed — reloading loads the
              authoritative state and never resimulates.
            </p>
          </>
        )}
      </div>
    </div>
  );
}
