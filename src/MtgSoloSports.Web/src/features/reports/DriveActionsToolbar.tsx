import { useState } from 'react';
import { apiErrorMessage } from '../../shared/api/http';
import {
  previewAthleteLibrary,
  previewDriveLibrary,
  publishDriveReports,
  syncAthleteLibrary,
  syncDriveLibrary,
  verifyDriveDestination,
  type DriveLibrarySyncResponse,
  type DriveVerifyResponse,
  type PublishDriveResponse,
} from './drivePublishingApi';
import { aggregateLibrarySync, type DriveLibrarySyncPage } from './driveLibrarySync';
import type { DriveSectionControls } from './DrivePublishingCard';

export interface DriveActionResultSetters {
  setPublishResult: (result: PublishDriveResponse | null) => void;
  setLibraryResult: (result: DriveLibrarySyncResponse | null) => void;
  setVerifyResult: (result: DriveVerifyResponse | null) => void;
}

/** Verify, publish and library sync actions with their safety checkboxes. */
export function DriveActionsToolbar({
  drive,
  confirmCrossSave,
  setConfirmCrossSave,
  results,
}: {
  drive: DriveSectionControls;
  confirmCrossSave: boolean;
  setConfirmCrossSave: (enabled: boolean) => void;
  results: DriveActionResultSetters;
}) {
  const [allowOverwrite, setAllowOverwrite] = useState(false);
  const [allowCreateMissing, setAllowCreateMissing] = useState(false);

  function runPagedLibrarySync(busyKey: 'library-sync' | 'athlete-sync', syncPage: DriveLibrarySyncPage): void {
    drive.setBusy(busyKey);
    drive.setError(null);
    results.setLibraryResult(null);
    void (async () => {
      const aggregated = await aggregateLibrarySync(syncPage, {
        confirmCrossSave,
        allowOverwriteEditedRemote: allowOverwrite,
        allowCreateMissing,
      });
      if (aggregated !== null) {
        results.setLibraryResult(aggregated);
      }
      await drive.refresh();
    })()
      .catch((failure: unknown) => drive.setError(apiErrorMessage(failure)))
      .finally(() => drive.setBusy(null));
  }

  return (
    <div className="toolbar" role="group" aria-label="Drive publish">
      <label className="field">
        <span>
          <input
            type="checkbox"
            checked={confirmCrossSave}
            disabled={drive.busy !== null}
            onChange={(event) => setConfirmCrossSave(event.target.checked)}
          />{' '}
          Confirm cross-save use
        </span>
      </label>
      <label className="field">
        <span>
          <input
            type="checkbox"
            checked={allowOverwrite}
            disabled={drive.busy !== null}
            onChange={(event) => setAllowOverwrite(event.target.checked)}
          />{' '}
          Overwrite independently edited Drive files
        </span>
      </label>
      <label className="field">
        <span>
          <input
            type="checkbox"
            checked={allowCreateMissing}
            disabled={drive.busy !== null}
            onChange={(event) => setAllowCreateMissing(event.target.checked)}
          />{' '}
          Allow creating missing folders/files
        </span>
      </label>
      <button
        type="button"
        className="ghost-button"
        disabled={drive.busy !== null || !drive.status.connected}
        onClick={() => {
          drive.setBusy('verify');
          drive.setError(null);
          results.setVerifyResult(null);
          void verifyDriveDestination(drive.saveId)
            .then((result) => results.setVerifyResult(result))
            .catch((failure: unknown) => drive.setError(apiErrorMessage(failure)))
            .finally(() => drive.setBusy(null));
        }}
      >
        {drive.busy === 'verify' ? 'Verifying…' : 'Verify access'}
      </button>
      <button
        type="button"
        className="ghost-button"
        disabled={drive.busy !== null || !drive.status.connected}
        onClick={() => {
          drive.setBusy('publish');
          drive.setError(null);
          results.setPublishResult(null);
          void publishDriveReports(drive.saveId, {
            confirmCrossSave,
            allowOverwriteEditedRemote: allowOverwrite,
            allowCreateMissing,
          })
            .then((result) => {
              results.setPublishResult(result);
              return drive.refresh();
            })
            .catch((failure: unknown) => drive.setError(apiErrorMessage(failure)))
            .finally(() => drive.setBusy(null));
        }}
      >
        {drive.busy === 'publish' ? 'Publishing…' : 'Publish now'}
      </button>
      <button
        type="button"
        className="ghost-button"
        disabled={drive.busy !== null || !drive.status.connected}
        onClick={() => {
          drive.setBusy('library-preview');
          drive.setError(null);
          results.setLibraryResult(null);
          void previewDriveLibrary(drive.saveId, {
            confirmCrossSave,
            allowOverwriteEditedRemote: allowOverwrite,
            allowCreateMissing,
          })
            .then((result) => results.setLibraryResult(result))
            .catch((failure: unknown) => drive.setError(apiErrorMessage(failure)))
            .finally(() => drive.setBusy(null));
        }}
      >
        {drive.busy === 'library-preview' ? 'Previewing…' : 'Preview library'}
      </button>
      <button
        type="button"
        className="ghost-button"
        disabled={drive.busy !== null || !drive.status.connected}
        onClick={() => {
          runPagedLibrarySync('library-sync', (options) => syncDriveLibrary(drive.saveId, options));
        }}
      >
        {drive.busy === 'library-sync' ? 'Syncing…' : 'Sync all changed reports'}
      </button>
      <button
        type="button"
        className="ghost-button"
        disabled={drive.busy !== null || !drive.status.connected}
        onClick={() => {
          drive.setBusy('athlete-preview');
          drive.setError(null);
          results.setLibraryResult(null);
          void previewAthleteLibrary(drive.saveId, {
            confirmCrossSave,
            allowOverwriteEditedRemote: allowOverwrite,
            allowCreateMissing,
          })
            .then((result) => results.setLibraryResult(result))
            .catch((failure: unknown) => drive.setError(apiErrorMessage(failure)))
            .finally(() => drive.setBusy(null));
        }}
      >
        {drive.busy === 'athlete-preview' ? 'Previewing…' : 'Preview athletes'}
      </button>
      <button
        type="button"
        className="ghost-button"
        disabled={drive.busy !== null || !drive.status.connected}
        onClick={() => {
          runPagedLibrarySync('athlete-sync', (options) => syncAthleteLibrary(drive.saveId, options));
        }}
      >
        {drive.busy === 'athlete-sync' ? 'Syncing…' : 'Publish all athletes / Sync changed athletes'}
      </button>
    </div>
  );
}
