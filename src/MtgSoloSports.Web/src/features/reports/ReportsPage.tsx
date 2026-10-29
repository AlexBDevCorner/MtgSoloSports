import { useEffect, useState } from 'react';
import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import { ApiError, apiErrorMessage, fetchJson } from '../../shared/api/http';
import { downloadReport, seasonDossierReportUrl, universeAlmanacReportUrl } from '../reports/reportApi';
import {
  disconnectDrive,
  getDriveStatus,
  publishDriveReports,
  saveDriveDestination,
  startDriveConnect,
  verifyDriveDestination,
  type DriveStatus,
  type DriveVerifyResponse,
  type PublishDriveResponse,
} from './drivePublishingApi';
import type { HistorySeasons } from '../history/historyApi';

const JOURNALIST_PROMPT = `You are a longtime MtgSoloSports columnist. Use only facts from the attached season dossier (current season) and universe almanac (history). Distinguish official points (season championship points) from weighted career points; Type Cup qualification never awards titles; Color individual and team events are separate titles; provisional tables are not final. Call out defending champions, streaks, upsets, qualification cut lines, and historical firsts with season numbers. If a fact is not in the sources, say it is unavailable rather than guessing.`;

async function fetchSeasons(saveId: string, signal: AbortSignal): Promise<HistorySeasons> {
  return fetchJson<HistorySeasons>(`/api/saves/${saveId}/history/seasons`, { signal });
}

export function ReportsPage({ saveId }: { saveId: string }) {
  const [seasons, setSeasons] = useState<HistorySeasons | null>(null);
  const [seasonsError, setSeasonsError] = useState<string | null>(null);
  const [seasonsLoading, setSeasonsLoading] = useState(true);
  const [selectedSeason, setSelectedSeason] = useState<number | null>(null);
  const [downloading, setDownloading] = useState<string | null>(null);
  const [downloadError, setDownloadError] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);

  const [driveStatus, setDriveStatus] = useState<DriveStatus | null>(null);
  const [driveLoading, setDriveLoading] = useState(true);
  const [driveError, setDriveError] = useState<string | null>(null);
  const [driveBusy, setDriveBusy] = useState<string | null>(null);
  const [driveNotice, setDriveNotice] = useState<string | null>(null);
  const [folderId, setFolderId] = useState('');
  const [folderName, setFolderName] = useState('');
  const [dossierFileId, setDossierFileId] = useState('');
  const [dossierFileName, setDossierFileName] = useState('');
  const [almanacFileId, setAlmanacFileId] = useState('');
  const [almanacFileName, setAlmanacFileName] = useState('');
  const [confirmCrossSave, setConfirmCrossSave] = useState(false);
  const [allowOverwrite, setAllowOverwrite] = useState(false);
  const [publishResult, setPublishResult] = useState<PublishDriveResponse | null>(null);
  const [verifyResult, setVerifyResult] = useState<DriveVerifyResponse | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    setSeasonsLoading(true);
    setSeasonsError(null);
    fetchSeasons(saveId, controller.signal)
      .then((loaded) => {
        if (controller.signal.aborted) {
          return;
        }
        const valid = loaded.saveId === saveId ? loaded : null;
        setSeasons(valid);
        const numbers = (valid?.seasons ?? []).map((row) => row.seasonNumber).sort((a, b) => a - b);
        setSelectedSeason(numbers.length > 0 ? numbers[numbers.length - 1]! : null);
        setSeasonsLoading(false);
      })
      .catch((failure: unknown) => {
        if (controller.signal.aborted) {
          return;
        }
        if (failure instanceof ApiError && failure.status === 404) {
          setSeasons(null);
          setSeasonsError(null);
        } else {
          setSeasonsError(apiErrorMessage(failure));
        }
        setSeasonsLoading(false);
      });
    return () => controller.abort();
  }, [saveId]);

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
        setFolderId(status.mtshistoryFolderId ?? '');
        setFolderName(status.mtshistoryFolderName ?? '');
        setDossierFileId(status.dossier.fileId ?? '');
        setDossierFileName(status.dossier.fileName ?? '');
        setAlmanacFileId(status.almanac.fileId ?? '');
        setAlmanacFileName(status.almanac.fileName ?? '');
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
  }, [saveId]);

  async function refreshDriveStatus(): Promise<void> {
    try {
      const status = await getDriveStatus(saveId);
      if (status.saveId !== saveId) {
        return;
      }
      setDriveStatus(status);
      setFolderId(status.mtshistoryFolderId ?? '');
      setFolderName(status.mtshistoryFolderName ?? '');
      setDossierFileId(status.dossier.fileId ?? '');
      setDossierFileName(status.dossier.fileName ?? '');
      setAlmanacFileId(status.almanac.fileId ?? '');
      setAlmanacFileName(status.almanac.fileName ?? '');
    } catch (failure) {
      setDriveError(apiErrorMessage(failure));
    }
  }

  async function handleDownload(url: string, fallback: string, key: string): Promise<void> {
    if (downloading !== null) {
      return;
    }
    setDownloading(key);
    setDownloadError(null);
    try {
      await downloadReport(url, fallback);
    } catch (failure) {
      setDownloadError(apiErrorMessage(failure));
    } finally {
      setDownloading(null);
    }
  }

  async function handleCopyPrompt(): Promise<void> {
    try {
      await navigator.clipboard.writeText(JOURNALIST_PROMPT);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } catch {
      setCopied(false);
    }
  }

  const seasonOptions = (seasons?.seasons ?? []).slice().sort((a, b) => a.seasonNumber - b.seasonNumber);

  return (
    <div className="dashboard reports-page">
      <Card eyebrow="Export data" title="Reports for ChatGPT">
        <p>
          Download a season dossier for one season and the universe almanac for all-time reference. Both are
          versioned source packs (Markdown for reading, JSON for analysis) using the same contracts as the
          athlete, standings, Cup and league reports.
        </p>
        <p className="muted small">
          Attach the files to a ChatGPT Project or chat. This app never syncs projects automatically; uploads
          become stale as simulation advances, so re-export the almanac occasionally and a fresh dossier for new
          coverage.
        </p>
      </Card>

      {seasonsLoading ? <Loading label="Loading seasons…" /> : null}
      {seasonsError ? (
        <Notice tone="error" title="Seasons unavailable">
          <p>{seasonsError}</p>
        </Notice>
      ) : null}

      <Card eyebrow="Season dossier" title="One season, journalism-ready">
        <p className="muted small">
          Full Superleague table, stage-by-stage winners, feeder summaries, movement/qualifier status, completed
          Cup summaries (Type qualification vs Final and Color individual vs team kept separate), defending
          champions, leader/Cup-performer career titles and official career records. Provisional and
          not-yet-held sections are labelled explicitly and never inferred.
        </p>
        <div className="toolbar" role="group" aria-label="Season dossier downloads">
          <label className="field">
            <span>Season</span>
            <select
              value={selectedSeason ?? ''}
              disabled={seasonOptions.length === 0 || downloading !== null}
              onChange={(event) => {
                const next = Number.parseInt(event.target.value, 10);
                setSelectedSeason(Number.isNaN(next) ? null : next);
              }}
            >
              {seasonOptions.map((row) => (
                <option key={row.seasonNumber} value={row.seasonNumber}>
                  Season {row.seasonNumber}
                  {row.isComplete ? '' : ' (in progress)'}
                </option>
              ))}
            </select>
          </label>
          <button
            type="button"
            className="ghost-button"
            disabled={selectedSeason === null || downloading !== null}
            onClick={() => {
              if (selectedSeason === null) {
                return;
              }
              void handleDownload(
                seasonDossierReportUrl(saveId, selectedSeason, 'md'),
                `season-${selectedSeason}-dossier.md`,
                `dossier-md-${selectedSeason}`,
              );
            }}
          >
            {downloading?.startsWith('dossier-md-') ? 'Preparing…' : 'Dossier .md'}
          </button>
          <button
            type="button"
            className="ghost-button"
            disabled={selectedSeason === null || downloading !== null}
            onClick={() => {
              if (selectedSeason === null) {
                return;
              }
              void handleDownload(
                seasonDossierReportUrl(saveId, selectedSeason, 'json'),
                `season-${selectedSeason}-dossier.json`,
                `dossier-json-${selectedSeason}`,
              );
            }}
          >
            {downloading?.startsWith('dossier-json-') ? 'Preparing…' : 'Dossier .json'}
          </button>
        </div>
        {selectedSeason === null && !seasonsLoading ? (
          <p className="muted small">No seasons yet to export.</p>
        ) : null}
      </Card>

      <Card eyebrow="Universe almanac" title="All-time reference">
        <p className="muted small">
          Season champions and podiums, Color/Type Cup medal history, official records, all-time Career Leaders
          with labelled weighted career points plus Superleague points leaders as a separate statistic, and
          consecutive title streaks. Covers active and inactive athletes.
        </p>
        <div className="toolbar" role="group" aria-label="Almanac downloads">
          <button
            type="button"
            className="ghost-button"
            disabled={downloading !== null}
            onClick={() => {
              void handleDownload(universeAlmanacReportUrl(saveId, 'md'), 'universe-almanac.md', 'almanac-md');
            }}
          >
            {downloading === 'almanac-md' ? 'Preparing…' : 'Almanac .md'}
          </button>
          <button
            type="button"
            className="ghost-button"
            disabled={downloading !== null}
            onClick={() => {
              void handleDownload(universeAlmanacReportUrl(saveId, 'json'), 'universe-almanac.json', 'almanac-json');
            }}
          >
            {downloading === 'almanac-json' ? 'Preparing…' : 'Almanac .json'}
          </button>
        </div>
      </Card>

      <Card eyebrow="Google Drive" title="Publish to Drive">
        <p className="muted small">
          Updates the current season dossier and universe almanac <em>in place</em> in your own{' '}
          <code>mtshistory</code> Drive hierarchy. Unchanged reports are skipped; retries never create
          duplicates. Publishing never changes simulation data.
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
        {driveStatus && !driveLoading ? (
          <>
            <p className="muted small">
              {driveStatus.configured ? 'Server is configured for Google OAuth.' : 'Server is not configured for Google OAuth — see setup docs.'}{' '}
              {driveStatus.connected
                ? `Connected${driveStatus.scopes ? ` (${driveStatus.scopes})` : ''}.`
                : 'Not connected.'}
            </p>
            <div className="toolbar" role="group" aria-label="Drive connection">
              <button
                type="button"
                className="ghost-button"
                disabled={driveBusy !== null || !driveStatus.configured}
                onClick={() => {
                  setDriveBusy('connect');
                  setDriveError(null);
                  void startDriveConnect(saveId).catch((failure: unknown) => {
                    setDriveError(apiErrorMessage(failure));
                    setDriveBusy(null);
                  });
                }}
              >
                {driveBusy === 'connect' ? 'Redirecting…' : 'Connect Google Drive'}
              </button>
              <button
                type="button"
                className="ghost-button"
                disabled={driveBusy !== null || !driveStatus.connected}
                onClick={() => {
                  setDriveBusy('disconnect');
                  setDriveError(null);
                  void disconnectDrive(saveId)
                    .then(() => refreshDriveStatus())
                    .catch((failure: unknown) => setDriveError(apiErrorMessage(failure)))
                    .finally(() => setDriveBusy(null));
                }}
              >
                {driveBusy === 'disconnect' ? 'Disconnecting…' : 'Disconnect'}
              </button>
            </div>
            <div className="toolbar" role="group" aria-label="Drive destination">
              <label className="field">
                <span>mtshistory folder id</span>
                <input
                  type="text"
                  value={folderId}
                  disabled={driveBusy !== null}
                  placeholder="Drive folder id for mtshistory"
                  onChange={(event) => setFolderId(event.target.value)}
                />
              </label>
              <label className="field">
                <span>Folder display name</span>
                <input
                  type="text"
                  value={folderName}
                  disabled={driveBusy !== null}
                  placeholder="mtshistory"
                  onChange={(event) => setFolderName(event.target.value)}
                />
              </label>
              <label className="field">
                <span>Dossier file id (Seasons/)</span>
                <input
                  type="text"
                  value={dossierFileId}
                  disabled={driveBusy !== null}
                  placeholder="adopted season dossier id"
                  onChange={(event) => setDossierFileId(event.target.value)}
                />
              </label>
              <label className="field">
                <span>Dossier file name</span>
                <input
                  type="text"
                  value={dossierFileName}
                  disabled={driveBusy !== null}
                  placeholder="season-N-dossier.md"
                  onChange={(event) => setDossierFileName(event.target.value)}
                />
              </label>
              <label className="field">
                <span>Almanac file id (root)</span>
                <input
                  type="text"
                  value={almanacFileId}
                  disabled={driveBusy !== null}
                  placeholder="adopted almanac id"
                  onChange={(event) => setAlmanacFileId(event.target.value)}
                />
              </label>
              <label className="field">
                <span>Almanac file name</span>
                <input
                  type="text"
                  value={almanacFileName}
                  disabled={driveBusy !== null}
                  placeholder="universe-almanac-….md"
                  onChange={(event) => setAlmanacFileName(event.target.value)}
                />
              </label>
            </div>
            <p>
              <button
                type="button"
                className="ghost-button"
                disabled={driveBusy !== null}
                onClick={() => {
                  setDriveBusy('save');
                  setDriveError(null);
                  void saveDriveDestination(saveId, {
                    mtshistoryFolderId: folderId,
                    mtshistoryFolderName: folderName,
                    dossierFileId,
                    dossierFileName,
                    almanacFileId,
                    almanacFileName,
                    confirmCrossSave,
                  })
                    .then(() => refreshDriveStatus())
                    .catch((failure: unknown) => setDriveError(apiErrorMessage(failure)))
                    .finally(() => setDriveBusy(null));
                }}
              >
                {driveBusy === 'save' ? 'Saving…' : 'Save destination'}
              </button>
            </p>
            <p className="muted small">
              Pasting a Drive id alone does not grant access. With the least-privilege <code>drive.file</code>{' '}
              scope the app can only use files it created or that you explicitly granted via Google Picker or
              Drive&apos;s <em>Open with</em>. After pasting ids, use <em>Verify access</em> to check each grant;
              each season keeps its own dossier file (<code>Seasons/season-N-dossier.md</code>) so a new season
              never overwrites the prior season&apos;s file.
            </p>
            <div className="toolbar" role="group" aria-label="Drive publish">
              <label className="field">
                <span>
                  <input
                    type="checkbox"
                    checked={confirmCrossSave}
                    disabled={driveBusy !== null}
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
                    disabled={driveBusy !== null}
                    onChange={(event) => setAllowOverwrite(event.target.checked)}
                  />{' '}
                  Overwrite independently edited Drive files
                </span>
              </label>
              <button
                type="button"
                className="ghost-button"
                disabled={driveBusy !== null || !driveStatus.connected}
                onClick={() => {
                  setDriveBusy('verify');
                  setDriveError(null);
                  setVerifyResult(null);
                  void verifyDriveDestination(saveId)
                    .then((result) => setVerifyResult(result))
                    .catch((failure: unknown) => setDriveError(apiErrorMessage(failure)))
                    .finally(() => setDriveBusy(null));
                }}
              >
                {driveBusy === 'verify' ? 'Verifying…' : 'Verify access'}
              </button>
              <button
                type="button"
                className="ghost-button"
                disabled={driveBusy !== null || !driveStatus.connected}
                onClick={() => {
                  setDriveBusy('publish');
                  setDriveError(null);
                  setPublishResult(null);
                  void publishDriveReports(saveId, {
                    confirmCrossSave,
                    allowOverwriteEditedRemote: allowOverwrite,
                  })
                    .then((result) => {
                      setPublishResult(result);
                      return refreshDriveStatus();
                    })
                    .catch((failure: unknown) => setDriveError(apiErrorMessage(failure)))
                    .finally(() => setDriveBusy(null));
                }}
              >
                {driveBusy === 'publish' ? 'Publishing…' : 'Publish now'}
              </button>
            </div>
            {driveStatus.lastPublishedUtc ? (
              <p className="muted small">
                Last publish: season {driveStatus.lastSeasonNumber} · {driveStatus.lastPhase} ·{' '}
                {driveStatus.lastPublishedUtc} · dossier {driveStatus.dossier.lastOutcome ?? '—'} · almanac{' '}
                {driveStatus.almanac.lastOutcome ?? '—'}
                {driveStatus.lastError ? ` · error: ${driveStatus.lastError}` : ''}
              </p>
            ) : null}
            {publishResult ? (
              <ul className="muted small">
                <li>
                  Season {publishResult.seasonNumber} · {publishResult.phase}
                  {publishResult.globalStage !== null ? ` · stage ${publishResult.globalStage}` : ''}
                </li>
                {publishResult.files.map((file) => (
                  <li key={file.report}>
                    {file.report}: {file.outcome}
                    {file.fileName ? ` — ${file.fileName}` : ''}
                    {file.detail ? ` — ${file.detail}` : ''}
                  </li>
                ))}
              </ul>
            ) : null}
            {verifyResult ? (
              <ul className="muted small">
                <li>Verify: season {verifyResult.currentSeason}</li>
                {verifyResult.checks.map((check) => (
                  <li key={check.report}>
                    {check.report}: {check.status}
                    {check.fileName ? ` — ${check.fileName}` : ''}
                    {check.detail ? ` — ${check.detail}` : ''}
                  </li>
                ))}
              </ul>
            ) : null}
          </>
        ) : null}
      </Card>

      {downloadError ? (
        <Notice tone="error" title="Report download failed">
          <p>{downloadError}</p>
        </Notice>
      ) : null}

      <Card eyebrow="Workflow" title="How to brief ChatGPT">
        <ol className="muted">
          <li>Export the almanac occasionally as a stable history reference.</li>
          <li>Export a fresh season dossier whenever you want coverage of a new stage or Cup.</li>
          <li>Attach both files to a ChatGPT Project or a new chat; replace the dossier as the save advances.</li>
        </ol>
        <p className="muted small">
          Reports provide facts and evidence, not prewritten opinions. Scoring note: official points are season
          totals in the dossier tables; weighted career points appear only in the almanac leaders and never
          decide a season title.
        </p>
        <label className="field">
          <span>Example journalist prompt (copy into ChatGPT; no narrative is generated in-app)</span>
          <pre className="muted small" style={{ whiteSpace: 'pre-wrap' }}>
            {JOURNALIST_PROMPT}
          </pre>
        </label>
        <p>
          <button type="button" className="ghost-button" onClick={() => void handleCopyPrompt()}>
            {copied ? 'Copied!' : 'Copy prompt'}
          </button>
        </p>
      </Card>
    </div>
  );
}
