import { useEffect, useState } from 'react';
import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import { apiErrorMessage } from '../../shared/api/http';
import {
  disconnectDrive,
  getDriveStatus,
  saveDriveDestination,
  startDriveConnect,
  type DriveLibrarySyncResponse,
  type DriveStatus,
  type DriveVerifyResponse,
  type PublishDriveResponse,
} from './drivePublishingApi';
import { DriveActionsToolbar, type DriveActionResultSetters } from './DriveActionsToolbar';
import { DriveAdoptionFields, parseAdoptionMap, useDriveAdoptions } from './DriveAdoptionFields';
import { DriveAutoPublishToolbar } from './DriveAutoPublishToolbar';
import { DriveDestinationFields } from './DriveDestinationFields';
import { DriveResultLists } from './DriveResultLists';
import { useDriveDestinationForm } from './useDriveDestinationForm';

/**
 * Explicit cross-section plumbing for the Drive publishing children: status,
 * busy key, error/notice surfacing and the post-action status refresh.
 */
export interface DriveSectionControls {
  saveId: string;
  status: DriveStatus;
  busy: string | null;
  setBusy: (key: string | null) => void;
  setError: (message: string | null) => void;
  setNotice: (message: string | null) => void;
  refresh: () => Promise<void>;
}

/** Google Drive publishing card: connection, destination, library sync and auto-publish. */
export function DrivePublishingCard({ saveId }: { saveId: string }) {
  const [driveStatus, setDriveStatus] = useState<DriveStatus | null>(null);
  const [driveLoading, setDriveLoading] = useState(true);
  const [driveError, setDriveError] = useState<string | null>(null);
  const [driveNotice, setDriveNotice] = useState<string | null>(null);
  const [driveBusy, setDriveBusy] = useState<string | null>(null);
  const [confirmCrossSave, setConfirmCrossSave] = useState(false);
  const [publishResult, setPublishResult] = useState<PublishDriveResponse | null>(null);
  const [libraryResult, setLibraryResult] = useState<DriveLibrarySyncResponse | null>(null);
  const [verifyResult, setVerifyResult] = useState<DriveVerifyResponse | null>(null);
  const destination = useDriveDestinationForm();
  const applyDestinationStatus = destination.applyStatus;
  const adoptions = useDriveAdoptions();

  useEffect(() => {
    const params = new URLSearchParams(window.location.search);
    const flag = params.get('drive');
    if (flag === 'connected') {
      setDriveNotice('Google Drive connected. Adopt the mtshistory folder and report files below, then Publish now.');
      window.history.replaceState(null, '', window.location.pathname);
    } else if (flag === 'error') {
      setDriveNotice(`Google Drive connection failed: ${params.get('message') ?? 'unknown error'}`);
      window.history.replaceState(null, '', window.location.pathname);
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    setDriveLoading(true);
    setDriveError(null);
    getDriveStatus(saveId, controller.signal)
      .then((status) => {
        if (controller.signal.aborted) {
          return;
        }
        setDriveStatus(status.saveId === saveId ? status : null);
        applyDestinationStatus(status);
        setDriveLoading(false);
      })
      .catch((failure: unknown) => {
        if (controller.signal.aborted) {
          return;
        }
        setDriveError(apiErrorMessage(failure));
        setDriveLoading(false);
      });
    return () => controller.abort();
    // applyDestinationStatus is the stable useCallback from
    // useDriveDestinationForm; keying on it (not the per-render destination
    // object) keeps this effect on the save identity alone.
  }, [saveId, applyDestinationStatus]);

  async function refreshDriveStatus(): Promise<void> {
    try {
      const status = await getDriveStatus(saveId);
      if (status.saveId !== saveId) {
        return;
      }
      setDriveStatus(status);
      destination.applyStatus(status);
    } catch (failure) {
      setDriveError(apiErrorMessage(failure));
    }
  }

  function handleSaveDestination(): void {
    setDriveBusy('save');
    setDriveError(null);
    let dossierAdoptions: Record<string, { fileId: string | null; fileName: string | null }> | null = null;
    let editionAdoptions: Record<string, { fileId: string | null; fileName: string | null }> | null = null;
    let teamAdoptions: Record<string, { fileId: string | null; fileName: string | null }> | null = null;
    let athleteAdoptions: Record<string, { fileId: string | null; fileName: string | null }> | null = null;
    try {
      dossierAdoptions = parseAdoptionMap(adoptions.dossierText, 'Dossier adoptions');
      editionAdoptions = parseAdoptionMap(adoptions.editionText, 'Edition adoptions');
      teamAdoptions = parseAdoptionMap(adoptions.teamText, 'Team adoptions');
      athleteAdoptions = parseAdoptionMap(adoptions.athleteText, 'Athlete adoptions');
    } catch (failure) {
      setDriveError(failure instanceof Error ? failure.message : 'Invalid adoption JSON.');
      setDriveBusy(null);
      return;
    }
    void saveDriveDestination(saveId, {
      mtshistoryFolderId: destination.form.mtshistoryFolderId,
      mtshistoryFolderName: destination.form.mtshistoryFolderName,
      seasonsFolderId: destination.form.seasonsFolderId,
      dossierFileId: destination.form.dossierFileId,
      dossierFileName: destination.form.dossierFileName,
      almanacFileId: destination.form.almanacFileId,
      almanacFileName: destination.form.almanacFileName,
      confirmCrossSave,
      colorCupFolderId: destination.form.colorCupFolderId,
      colorCupsFolderId: destination.form.colorCupsFolderId,
      colorTeamsFolderId: destination.form.colorTeamsFolderId,
      typeCupFolderId: destination.form.typeCupFolderId,
      typeCupsFolderId: destination.form.typeCupsFolderId,
      typeTeamsFolderId: destination.form.typeTeamsFolderId,
      chaosCupFolderId: destination.form.chaosCupFolderId,
      chaosCupsFolderId: destination.form.chaosCupsFolderId,
      dossierAdoptions,
      editionAdoptions,
      teamAdoptions,
      athletesFolderId: destination.form.athletesFolderId,
      athletesFolderName: destination.form.athletesFolderName,
      athletesIndexFileId: destination.form.athletesIndexFileId,
      athletesIndexFileName: destination.form.athletesIndexFileName,
      athleteAdoptions,
    })
      .then(() => refreshDriveStatus())
      .catch((failure: unknown) => setDriveError(apiErrorMessage(failure)))
      .finally(() => setDriveBusy(null));
  }

  const controls: DriveSectionControls | null =
    driveStatus !== null && !driveLoading
      ? {
          saveId,
          status: driveStatus,
          busy: driveBusy,
          setBusy: setDriveBusy,
          setError: setDriveError,
          setNotice: setDriveNotice,
          refresh: refreshDriveStatus,
        }
      : null;

  const results: DriveActionResultSetters = { setPublishResult, setLibraryResult, setVerifyResult };

  return (
    <Card eyebrow="Google Drive" title="Publish to Drive">
      <p className="muted small">
        Updates the current season dossier and universe almanac <em>in place</em> in your own{' '}
        <code>mtshistory</code> Drive hierarchy. <em>Sync all changed reports</em> extends the same
        in-place rules to every season dossier, Color/Type/Chaos Cup edition and Color/Type team history. Unchanged
        reports are skipped; retries never create duplicates. Publishing never changes simulation data.
      </p>
      {driveLoading ? <Loading label="Loading Drive status…" /> : null}
      {driveNotice ? (
        <Notice tone="info" title="Google Drive">
          <p>{driveNotice}</p>
        </Notice>
      ) : null}
      {driveError ? (
        <Notice tone="error" title="Drive status unavailable">
          <p>{driveError}</p>
        </Notice>
      ) : null}
      {controls ? (
        <>
          <p className="muted small">
            {controls.status.configured ? 'Server is configured for Google OAuth.' : 'Server is not configured for Google OAuth — see setup docs.'}{' '}
            {controls.status.connected
              ? `Connected${controls.status.scopes ? ` (${controls.status.scopes})` : ''}.`
              : 'Not connected.'}
          </p>
          <div className="toolbar" role="group" aria-label="Drive connection">
            <button
              type="button"
              className="ghost-button"
              disabled={controls.busy !== null || !controls.status.configured}
              onClick={() => {
                controls.setBusy('connect');
                controls.setError(null);
                void startDriveConnect(saveId).catch((failure: unknown) => {
                  controls.setError(apiErrorMessage(failure));
                  controls.setBusy(null);
                });
              }}
            >
              {controls.busy === 'connect' ? 'Redirecting…' : 'Connect Google Drive'}
            </button>
            <button
              type="button"
              className="ghost-button"
              disabled={controls.busy !== null || !controls.status.connected}
              onClick={() => {
                controls.setBusy('disconnect');
                controls.setError(null);
                void disconnectDrive(saveId)
                  .then(() => controls.refresh())
                  .catch((failure: unknown) => controls.setError(apiErrorMessage(failure)))
                  .finally(() => controls.setBusy(null));
              }}
            >
              {controls.busy === 'disconnect' ? 'Disconnecting…' : 'Disconnect'}
            </button>
          </div>
          <DriveDestinationFields drive={controls} form={destination.form} setField={destination.setField} />
          <DriveAdoptionFields
            drive={controls}
            adoptions={adoptions}
            onSaveDestination={handleSaveDestination}
          />
          <DriveActionsToolbar
            drive={controls}
            confirmCrossSave={confirmCrossSave}
            setConfirmCrossSave={setConfirmCrossSave}
            results={results}
          />
          <p className="muted small">
            Publish now stays dossier + almanac only. The athlete archive (one file per athlete in{' '}
            <code>Athletes/White…Colorless</code> plus <code>athletes-index.md</code>) needs its explicit
            first bulk import above; ordinary runs are incremental. A Drive folder with thousands of reports
            is not ChatGPT Project uploads — use the index with ChatGPT&apos;s Drive picker for explicit
            retrieval.
          </p>
          {controls.status.athletesFolderId ?? controls.status.athletesIndexFileId ? (
            <p className="muted small">
              Athletes:{' '}
              {controls.status.athletesFolderId ? `folder ${controls.status.athletesFolderId}` : 'folder not adopted'}
              {controls.status.athletesIndexFileId ? ` · index ${controls.status.athletesIndexFileId}` : ' · index not published'}
            </p>
          ) : null}
          {controls.status.lastLibraryPublishedUtc ?? controls.status.lastLibraryError ? (
            <p className="muted small">
              Library:{' '}
              {controls.status.lastLibraryPublishedUtc
                ? `synced ${controls.status.lastLibraryPublishedUtc}`
                : 'never fully synced'}
              {` · +${controls.status.lastLibraryCreated} ~${controls.status.lastLibraryUpdated} =${controls.status.lastLibraryUnchanged} !${controls.status.lastLibraryFailed}`}
              {controls.status.lastLibraryError ? ` · error: ${controls.status.lastLibraryError}` : ''}
            </p>
          ) : null}
          {controls.status.lastPublishedUtc ? (
            <p className="muted small">
              Last publish: season {controls.status.lastSeasonNumber} · {controls.status.lastPhase} ·{' '}
              {controls.status.lastPublishedUtc} · dossier {controls.status.dossier.lastOutcome ?? '—'} · almanac{' '}
              {controls.status.almanac.lastOutcome ?? '—'}
              {controls.status.lastError ? ` · error: ${controls.status.lastError}` : ''}
            </p>
          ) : null}
          <DriveAutoPublishToolbar drive={controls} />
          <DriveResultLists publishResult={publishResult} libraryResult={libraryResult} verifyResult={verifyResult} />
        </>
      ) : null}
    </Card>
  );
}
