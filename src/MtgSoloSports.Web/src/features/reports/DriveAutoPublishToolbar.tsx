import { useState } from 'react';
import { apiErrorMessage } from '../../shared/api/http';
import { describeAutoPublishStatus, retryDriveAutoPublish, setDriveAutoPublish } from './drivePublishingApi';
import type { DriveSectionControls } from './DrivePublishingCard';

/** Auto-publish toggle, retry button and explanatory notes. */
export function DriveAutoPublishToolbar({ drive }: { drive: DriveSectionControls }) {
  const [autoBusy, setAutoBusy] = useState(false);
  const { status } = drive;

  return (
    <>
      <div className="toolbar" role="group" aria-label="Drive automatic publishing">
        <label className="field">
          <span>
            <input
              type="checkbox"
              checked={status.autoPublishEnabled}
              disabled={drive.busy !== null || autoBusy}
              onChange={(event) => {
                const enabled = event.target.checked;
                setAutoBusy(true);
                drive.setError(null);
                void setDriveAutoPublish(drive.saveId, enabled)
                  .then(() => drive.refresh())
                  .catch((failure: unknown) => drive.setError(apiErrorMessage(failure)))
                  .finally(() => setAutoBusy(false));
              }}
            />{' '}
            Automatically publish after each completed stage
          </span>
        </label>
        <span className="muted small" aria-live="polite">
          Auto-publish: {describeAutoPublishStatus(status.autoPublishStatus)}
          {status.autoRequestedSeason !== null
            ? ` · requested season ${status.autoRequestedSeason} · stage ${status.autoRequestedGlobalStage ?? '—'}`
            : ''}
        </span>
        <button
          type="button"
          className="ghost-button"
          disabled={drive.busy !== null || autoBusy || !status.autoPublishEnabled}
          onClick={() => {
            setAutoBusy(true);
            drive.setError(null);
            void retryDriveAutoPublish(drive.saveId)
              .then(() => drive.refresh())
              .catch((failure: unknown) => drive.setError(apiErrorMessage(failure)))
              .finally(() => setAutoBusy(false));
          }}
        >
          {autoBusy ? 'Working…' : 'Retry auto-publish'}
        </button>
      </div>
      {status.autoLastError ? (
        <p className="muted small">Auto-publish note: {status.autoLastError}</p>
      ) : null}
      {status.autoPublishStatus === 'Disconnected' && status.autoPublishEnabled ? (
        <p className="muted small">
          Automatic publishing is paused: reconnect Google Drive to resume. Manual Publish now stays
          available as an override.
        </p>
      ) : null}
      <p className="muted small">
        Automatic publishing uploads the current season dossier and universe almanac after each
        completed global stage while the app runs, plus the Color/Type Cup edition and team-history
        reports that actually changed. It never blocks simulation and never deletes
        Drive files when disabled. New seasons start a new stable dossier file; prior seasons are
        preserved.
      </p>
    </>
  );
}
