import { useEffect, useState } from 'react';
import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import { ApiError, apiErrorMessage, fetchJson } from '../../shared/api/http';
import { downloadReport, seasonDossierReportUrl, universeAlmanacReportUrl } from '../reports/reportApi';
import {
  describeAutoPublishStatus,
  disconnectDrive,
  getDriveStatus,
  mergeLibraryAdoptionEntry,
  previewAthleteLibrary,
  previewDriveLibrary,
  publishDriveReports,
  retryDriveAutoPublish,
  saveDriveDestination,
  setDriveAutoPublish,
  startDriveConnect,
  syncAthleteLibrary,
  syncDriveLibrary,
  verifyDriveDestination,
  type DriveLibrarySyncResponse,
  type DriveStatus,
  type DriveVerifyResponse,
  type PublishDriveResponse,
} from './drivePublishingApi';
import {
  pickerConfigFromStatus,
  pickerDisabledHint,
  showDrivePicker,
} from './drivePicker';
import type { HistorySeasons } from '../history/historyApi';

const JOURNALIST_PROMPT = `You are a longtime MtgSoloSports columnist. Use only facts from the attached season dossier (current season) and universe almanac (history). Distinguish official points (season championship points) from weighted career points; Type Cup qualification never awards titles; Color individual and team events are separate titles; provisional tables are not final. Call out defending champions, streaks, upsets, qualification cut lines, and historical firsts with season numbers. If a fact is not in the sources, say it is unavailable rather than guessing.`;

async function fetchSeasons(saveId: string, signal: AbortSignal): Promise<HistorySeasons> {
  return fetchJson<HistorySeasons>(`/api/saves/${saveId}/history/seasons`, { signal });
}

function mergeLibraryCategories(
  left: DriveLibrarySyncResponse['categories'],
  right: DriveLibrarySyncResponse['categories'],
): DriveLibrarySyncResponse['categories'] {
  const merged = new Map<string, DriveLibrarySyncResponse['categories'][number]>();
  for (const entry of [...left, ...right]) {
    const existing = merged.get(entry.category);
    merged.set(
      entry.category,
      existing === undefined
        ? entry
        : {
            category: entry.category,
            total: existing.total + entry.total,
            created: existing.created + entry.created,
            updated: existing.updated + entry.updated,
            unchanged: existing.unchanged + entry.unchanged,
            failed: existing.failed + entry.failed,
          },
    );
  }
  return [...merged.values()];
}

function parseAdoptionMap(
  raw: string,
  label: string,
): Record<string, { fileId: string | null; fileName: string | null }> | null {
  const trimmed = raw.trim();
  if (trimmed === '') {
    return null;
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(trimmed);
  } catch {
    throw new Error(`${label} is not valid JSON. Use {"key": {"fileId": "...", "fileName": "..."}}.`);
  }
  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
    throw new Error(`${label} must be a JSON object keyed by semantic report key.`);
  }
  const entries = Object.entries(parsed as Record<string, unknown>);
  if (entries.length > 500) {
    throw new Error(`${label} has too many entries (max 500).`);
  }
  const normalized: Record<string, { fileId: string | null; fileName: string | null }> = {};
  for (const [key, value] of entries) {
    if (key.trim() === '' || key.includes('/') || key.includes('\\')) {
      throw new Error(`${label} has an invalid key "${key}".`);
    }
    if (typeof value !== 'object' || value === null) {
      throw new Error(`${label} entry "${key}" must be {"fileId": "...", "fileName": "..."}.`);
    }
    const record = value as Record<string, unknown>;
    const fileId = record.fileId;
    const fileName = record.fileName;
    normalized[key.trim()] = {
      fileId: typeof fileId === 'string' && fileId.trim() !== '' ? fileId.trim() : null,
      fileName: typeof fileName === 'string' && fileName.trim() !== '' ? fileName.trim() : null,
    };
  }
  return normalized;
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
  const [seasonsFolderId, setSeasonsFolderId] = useState('');
  const [dossierFileId, setDossierFileId] = useState('');
  const [dossierFileName, setDossierFileName] = useState('');
  const [almanacFileId, setAlmanacFileId] = useState('');
  const [almanacFileName, setAlmanacFileName] = useState('');
  const [colorCupFolderId, setColorCupFolderId] = useState('');
  const [colorCupsFolderId, setColorCupsFolderId] = useState('');
  const [colorTeamsFolderId, setColorTeamsFolderId] = useState('');
  const [typeCupFolderId, setTypeCupFolderId] = useState('');
  const [typeCupsFolderId, setTypeCupsFolderId] = useState('');
  const [typeTeamsFolderId, setTypeTeamsFolderId] = useState('');
  const [chaosCupFolderId, setChaosCupFolderId] = useState('');
  const [chaosCupsFolderId, setChaosCupsFolderId] = useState('');
  const [athletesFolderId, setAthletesFolderId] = useState('');
  const [athletesFolderName, setAthletesFolderName] = useState('');
  const [athletesIndexFileId, setAthletesIndexFileId] = useState('');
  const [athletesIndexFileName, setAthletesIndexFileName] = useState('');
  const [athleteAdoptionsText, setAthleteAdoptionsText] = useState('');
  const [athleteAdoptionKey, setAthleteAdoptionKey] = useState('');
  const [dossierAdoptionsText, setDossierAdoptionsText] = useState('');
  const [editionAdoptionsText, setEditionAdoptionsText] = useState('');
  const [teamAdoptionsText, setTeamAdoptionsText] = useState('');
  const [dossierAdoptionKey, setDossierAdoptionKey] = useState('');
  const [editionAdoptionKey, setEditionAdoptionKey] = useState('');
  const [teamAdoptionKey, setTeamAdoptionKey] = useState('');
  const [confirmCrossSave, setConfirmCrossSave] = useState(false);
  const [allowOverwrite, setAllowOverwrite] = useState(false);
  const [allowCreateMissing, setAllowCreateMissing] = useState(false);
  const [autoBusy, setAutoBusy] = useState(false);
  const [publishResult, setPublishResult] = useState<PublishDriveResponse | null>(null);
  const [libraryResult, setLibraryResult] = useState<DriveLibrarySyncResponse | null>(null);
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
        setSeasonsFolderId(status.seasonsFolderId ?? '');
        setDossierFileId(status.dossier.fileId ?? '');
        setDossierFileName(status.dossier.fileName ?? '');
        setAlmanacFileId(status.almanac.fileId ?? '');
        setAlmanacFileName(status.almanac.fileName ?? '');
        setColorCupFolderId(status.colorCupFolderId ?? '');
        setColorCupsFolderId(status.colorCupsFolderId ?? '');
        setColorTeamsFolderId(status.colorTeamsFolderId ?? '');
        setTypeCupFolderId(status.typeCupFolderId ?? '');
        setTypeCupsFolderId(status.typeCupsFolderId ?? '');
        setTypeTeamsFolderId(status.typeTeamsFolderId ?? '');
        setChaosCupFolderId(status.chaosCupFolderId ?? '');
        setChaosCupsFolderId(status.chaosCupsFolderId ?? '');
        setAthletesFolderId(status.athletesFolderId ?? '');
        setAthletesFolderName(status.athletesFolderName ?? '');
        setAthletesIndexFileId(status.athletesIndexFileId ?? '');
        setAthletesIndexFileName(status.athletesIndexFileName ?? '');
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
      setSeasonsFolderId(status.seasonsFolderId ?? '');
      setDossierFileId(status.dossier.fileId ?? '');
      setDossierFileName(status.dossier.fileName ?? '');
      setAlmanacFileId(status.almanac.fileId ?? '');
      setAlmanacFileName(status.almanac.fileName ?? '');
      setColorCupFolderId(status.colorCupFolderId ?? '');
      setColorCupsFolderId(status.colorCupsFolderId ?? '');
      setColorTeamsFolderId(status.colorTeamsFolderId ?? '');
      setTypeCupFolderId(status.typeCupFolderId ?? '');
      setTypeCupsFolderId(status.typeCupsFolderId ?? '');
      setTypeTeamsFolderId(status.typeTeamsFolderId ?? '');
      setChaosCupFolderId(status.chaosCupFolderId ?? '');
      setChaosCupsFolderId(status.chaosCupsFolderId ?? '');
      setAthletesFolderId(status.athletesFolderId ?? '');
      setAthletesFolderName(status.athletesFolderName ?? '');
      setAthletesIndexFileId(status.athletesIndexFileId ?? '');
      setAthletesIndexFileName(status.athletesIndexFileName ?? '');
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
                <span>Seasons folder id (optional)</span>
                <input
                  type="text"
                  value={seasonsFolderId}
                  disabled={driveBusy !== null}
                  placeholder="Drive folder id for Seasons/"
                  onChange={(event) => setSeasonsFolderId(event.target.value)}
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
            <div className="toolbar" role="group" aria-label="Drive Cup library folders">
              <label className="field">
                <span>Color Cup folder id</span>
                <input
                  type="text"
                  value={colorCupFolderId}
                  disabled={driveBusy !== null}
                  placeholder="Drive folder id for Color Cup/"
                  onChange={(event) => setColorCupFolderId(event.target.value)}
                />
              </label>
              <label className="field">
                <span>Color Cups folder id</span>
                <input
                  type="text"
                  value={colorCupsFolderId}
                  disabled={driveBusy !== null}
                  placeholder="Drive folder id for Color Cup/Cups"
                  onChange={(event) => setColorCupsFolderId(event.target.value)}
                />
              </label>
              <label className="field">
                <span>Color Teams Histories folder id</span>
                <input
                  type="text"
                  value={colorTeamsFolderId}
                  disabled={driveBusy !== null}
                  placeholder="Drive folder id for Color Cup/Teams Histories"
                  onChange={(event) => setColorTeamsFolderId(event.target.value)}
                />
              </label>
              <label className="field">
                <span>Type Cup folder id</span>
                <input
                  type="text"
                  value={typeCupFolderId}
                  disabled={driveBusy !== null}
                  placeholder="Drive folder id for Type Cup/"
                  onChange={(event) => setTypeCupFolderId(event.target.value)}
                />
              </label>
              <label className="field">
                <span>Type Cups folder id</span>
                <input
                  type="text"
                  value={typeCupsFolderId}
                  disabled={driveBusy !== null}
                  placeholder="Drive folder id for Type Cup/Cups"
                  onChange={(event) => setTypeCupsFolderId(event.target.value)}
                />
              </label>
              <label className="field">
                <span>Type Teams Histories folder id</span>
                <input
                  type="text"
                  value={typeTeamsFolderId}
                  disabled={driveBusy !== null}
                  placeholder="Drive folder id for Type Cup/Teams Histories"
                  onChange={(event) => setTypeTeamsFolderId(event.target.value)}
                />
              </label>
              <label className="field">
                <span>Chaos Cup folder id</span>
                <input
                  type="text"
                  value={chaosCupFolderId}
                  disabled={driveBusy !== null}
                  placeholder="Drive folder id for Chaos Cup/"
                  onChange={(event) => setChaosCupFolderId(event.target.value)}
                />
              </label>
              <label className="field">
                <span>Chaos Cups folder id</span>
                <input
                  type="text"
                  value={chaosCupsFolderId}
                  disabled={driveBusy !== null}
                  placeholder="Drive folder id for Chaos Cup/Cups"
                  onChange={(event) => setChaosCupsFolderId(event.target.value)}
                />
              </label>
              <label className="field">
                <span>Athletes folder id</span>
                <input
                  type="text"
                  value={athletesFolderId}
                  disabled={driveBusy !== null}
                  placeholder="Drive folder id for Athletes/"
                  onChange={(event) => setAthletesFolderId(event.target.value)}
                />
              </label>
              <label className="field">
                <span>Athletes folder name</span>
                <input
                  type="text"
                  value={athletesFolderName}
                  disabled={driveBusy !== null}
                  placeholder="Athletes (custom root name allowed)"
                  onChange={(event) => setAthletesFolderName(event.target.value)}
                />
              </label>
              <label className="field">
                <span>Athletes index file id</span>
                <input
                  type="text"
                  value={athletesIndexFileId}
                  disabled={driveBusy !== null}
                  placeholder="Drive file id for athletes-index.md"
                  onChange={(event) => setAthletesIndexFileId(event.target.value)}
                />
              </label>
              <label className="field">
                <span>Athletes index file name</span>
                <input
                  type="text"
                  value={athletesIndexFileName}
                  disabled={driveBusy !== null}
                  placeholder="athletes-index.md"
                  onChange={(event) => setAthletesIndexFileName(event.target.value)}
                />
              </label>
            </div>
            <div className="toolbar" role="group" aria-label="Drive Picker grant">
              <button
                type="button"
                className="ghost-button"
                disabled={driveBusy !== null || pickerConfigFromStatus(driveStatus) === null}
                title={pickerDisabledHint(driveStatus)}
                onClick={() => {
                  const config = pickerConfigFromStatus(driveStatus);
                  if (!config) {
                    setDriveError(pickerDisabledHint(driveStatus));
                    return;
                  }
                  setDriveBusy('pick-folder');
                  setDriveError(null);
                  void showDrivePicker(config, 'folder')
                    .then((picked) => {
                      if (picked) {
                        setFolderId(picked.id);
                        if (picked.name) {
                          setFolderName(picked.name);
                        }
                        setDriveNotice(
                          `Picked folder “${picked.name}” — press Save destination, then Verify access.`,
                        );
                      }
                    })
                    .catch((failure: unknown) => setDriveError(apiErrorMessage(failure)))
                    .finally(() => setDriveBusy(null));
                }}
              >
                {driveBusy === 'pick-folder' ? 'Opening Picker…' : 'Choose folder…'}
              </button>
              <button
                type="button"
                className="ghost-button"
                disabled={driveBusy !== null || pickerConfigFromStatus(driveStatus) === null}
                title={pickerDisabledHint(driveStatus)}
                onClick={() => {
                  const config = pickerConfigFromStatus(driveStatus);
                  if (!config) {
                    setDriveError(pickerDisabledHint(driveStatus));
                    return;
                  }
                  setDriveBusy('pick-seasons');
                  setDriveError(null);
                  void showDrivePicker(config, 'folder')
                    .then((picked) => {
                      if (picked) {
                        setSeasonsFolderId(picked.id);
                        setDriveNotice(
                          `Picked Seasons folder “${picked.name}” — press Save destination, then Verify access.`,
                        );
                      }
                    })
                    .catch((failure: unknown) => setDriveError(apiErrorMessage(failure)))
                    .finally(() => setDriveBusy(null));
                }}
              >
                {driveBusy === 'pick-seasons' ? 'Opening Picker…' : 'Choose Seasons…'}
              </button>
              <button
                type="button"
                className="ghost-button"
                disabled={driveBusy !== null || pickerConfigFromStatus(driveStatus) === null}
                title={pickerDisabledHint(driveStatus)}
                onClick={() => {
                  const config = pickerConfigFromStatus(driveStatus);
                  if (!config) {
                    setDriveError(pickerDisabledHint(driveStatus));
                    return;
                  }
                  setDriveBusy('pick-dossier');
                  setDriveError(null);
                  void showDrivePicker(config, 'markdown')
                    .then((picked) => {
                      if (picked) {
                        setDossierFileId(picked.id);
                        if (picked.name) {
                          setDossierFileName(picked.name);
                        }
                        setDriveNotice(
                          `Picked dossier “${picked.name}” — press Save destination, then Verify access.`,
                        );
                      }
                    })
                    .catch((failure: unknown) => setDriveError(apiErrorMessage(failure)))
                    .finally(() => setDriveBusy(null));
                }}
              >
                {driveBusy === 'pick-dossier' ? 'Opening Picker…' : 'Choose dossier…'}
              </button>
              <button
                type="button"
                className="ghost-button"
                disabled={driveBusy !== null || pickerConfigFromStatus(driveStatus) === null}
                title={pickerDisabledHint(driveStatus)}
                onClick={() => {
                  const config = pickerConfigFromStatus(driveStatus);
                  if (!config) {
                    setDriveError(pickerDisabledHint(driveStatus));
                    return;
                  }
                  setDriveBusy('pick-almanac');
                  setDriveError(null);
                  void showDrivePicker(config, 'markdown')
                    .then((picked) => {
                      if (picked) {
                        setAlmanacFileId(picked.id);
                        if (picked.name) {
                          setAlmanacFileName(picked.name);
                        }
                        setDriveNotice(
                          `Picked almanac “${picked.name}” — press Save destination, then Verify access.`,
                        );
                      }
                    })
                    .catch((failure: unknown) => setDriveError(apiErrorMessage(failure)))
                    .finally(() => setDriveBusy(null));
                }}
              >
                {driveBusy === 'pick-almanac' ? 'Opening Picker…' : 'Choose almanac…'}
              </button>
            </div>
            <div className="toolbar" role="group" aria-label="Drive Cup library folder grants">
              {(
                [
                  ['pick-color-cup', 'Choose Color Cup…', 'colorCupFolderId'],
                  ['pick-color-cups', 'Choose Color Cups…', 'colorCupsFolderId'],
                  ['pick-color-teams', 'Choose Color Teams…', 'colorTeamsFolderId'],
                  ['pick-type-cup', 'Choose Type Cup…', 'typeCupFolderId'],
                  ['pick-type-cups', 'Choose Type Cups…', 'typeCupsFolderId'],
                  ['pick-type-teams', 'Choose Type Teams…', 'typeTeamsFolderId'],
                  ['pick-chaos-cup', 'Choose Chaos Cup…', 'chaosCupFolderId'],
                  ['pick-chaos-cups', 'Choose Chaos Cups…', 'chaosCupsFolderId'],
                ] as const
              ).map(([busyKey, label, target]) => (
                <button
                  key={target}
                  type="button"
                  className="ghost-button"
                  disabled={driveBusy !== null || pickerConfigFromStatus(driveStatus) === null}
                  title={pickerDisabledHint(driveStatus)}
                  onClick={() => {
                    const config = pickerConfigFromStatus(driveStatus);
                    if (!config) {
                      setDriveError(pickerDisabledHint(driveStatus));
                      return;
                    }
                    setDriveBusy(busyKey);
                    setDriveError(null);
                    void showDrivePicker(config, 'folder')
                      .then((picked) => {
                        if (picked) {
                          if (target === 'colorCupFolderId') setColorCupFolderId(picked.id);
                          else if (target === 'colorCupsFolderId') setColorCupsFolderId(picked.id);
                          else if (target === 'colorTeamsFolderId') setColorTeamsFolderId(picked.id);
                          else if (target === 'typeCupFolderId') setTypeCupFolderId(picked.id);
                          else if (target === 'typeCupsFolderId') setTypeCupsFolderId(picked.id);
                          else if (target === 'typeTeamsFolderId') setTypeTeamsFolderId(picked.id);
                          else if (target === 'chaosCupFolderId') setChaosCupFolderId(picked.id);
                          else setChaosCupsFolderId(picked.id);
                          setDriveNotice(
                            `Picked folder “${picked.name}” — press Save destination, then Verify access.`,
                          );
                        }
                      })
                      .catch((failure: unknown) => setDriveError(apiErrorMessage(failure)))
                      .finally(() => setDriveBusy(null));
                  }}
                >
                  {driveBusy === busyKey ? 'Opening Picker…' : label}
                </button>
              ))}
            </div>
            <p className="muted small">
              {driveStatus.pickerEnabled
                ? 'Choose from Drive grants drive.file access to that exact folder/file (same Google account), so publishing can update it in place.'
                : pickerDisabledHint(driveStatus)}
            </p>
            <div className="toolbar" role="group" aria-label="Drive library file adoptions">
              <label className="field">
                <span>Historical dossier adoptions (JSON)</span>
                <input
                  type="text"
                  value={dossierAdoptionsText}
                  disabled={driveBusy !== null}
                  placeholder='{"1": {"fileId": "...", "fileName": "season-1-dossier.md"}}'
                  onChange={(event) => setDossierAdoptionsText(event.target.value)}
                />
              </label>
              <label className="field">
                <span>Cup edition adoptions (JSON)</span>
                <input
                  type="text"
                  value={editionAdoptionsText}
                  disabled={driveBusy !== null}
                  placeholder='{"color:1": {"fileId": "...", "fileName": "my-edition.md"}}'
                  onChange={(event) => setEditionAdoptionsText(event.target.value)}
                />
              </label>
              <label className="field">
                <span>Cup team adoptions (JSON)</span>
                <input
                  type="text"
                  value={teamAdoptionsText}
                  disabled={driveBusy !== null}
                  placeholder='{"color:red": {"fileId": "...", "fileName": "my-team.md"}}'
                  onChange={(event) => setTeamAdoptionsText(event.target.value)}
                />
              </label>
              <label className="field">
                <span>Athlete adoptions (JSON)</span>
                <input
                  type="text"
                  value={athleteAdoptionsText}
                  disabled={driveBusy !== null}
                  placeholder='{"123": {"fileId": "...", "fileName": "athlete-123-....md"}}'
                  onChange={(event) => setAthleteAdoptionsText(event.target.value)}
                />
              </label>
            </div>
            <div className="toolbar" role="group" aria-label="Drive library file grants">
              <label className="field">
                <span>Season key to grant</span>
                <input
                  type="text"
                  value={dossierAdoptionKey}
                  disabled={driveBusy !== null}
                  placeholder="1"
                  onChange={(event) => setDossierAdoptionKey(event.target.value)}
                />
              </label>
              <button
                type="button"
                className="ghost-button"
                disabled={driveBusy !== null || pickerConfigFromStatus(driveStatus) === null}
                title={pickerDisabledHint(driveStatus)}
                onClick={() => {
                  const config = pickerConfigFromStatus(driveStatus);
                  if (!config) {
                    setDriveError(pickerDisabledHint(driveStatus));
                    return;
                  }
                  if (dossierAdoptionKey.trim() === '') {
                    setDriveError('Enter the season key (for example "1") before choosing its Drive file.');
                    return;
                  }
                  setDriveBusy('pick-dossier-file');
                  setDriveError(null);
                  void showDrivePicker(config, 'markdown')
                    .then((picked) => {
                      if (picked) {
                        try {
                          setDossierAdoptionsText((current) =>
                            mergeLibraryAdoptionEntry(current, dossierAdoptionKey, picked.id, picked.name),
                          );
                          setDriveNotice(
                            `Granted “${picked.name}” for season ${dossierAdoptionKey.trim()} — press Save destination, then Verify access.`,
                          );
                        } catch (failure) {
                          setDriveError(failure instanceof Error ? failure.message : 'Could not adopt the picked file.');
                        }
                      }
                    })
                    .catch((failure: unknown) => setDriveError(apiErrorMessage(failure)))
                    .finally(() => setDriveBusy(null));
                }}
              >
                {driveBusy === 'pick-dossier-file' ? 'Opening Picker…' : 'Choose season file…'}
              </button>
              <label className="field">
                <span>Edition key to grant</span>
                <input
                  type="text"
                  value={editionAdoptionKey}
                  disabled={driveBusy !== null}
                  placeholder="color:1"
                  onChange={(event) => setEditionAdoptionKey(event.target.value)}
                />
              </label>
              <button
                type="button"
                className="ghost-button"
                disabled={driveBusy !== null || pickerConfigFromStatus(driveStatus) === null}
                title={pickerDisabledHint(driveStatus)}
                onClick={() => {
                  const config = pickerConfigFromStatus(driveStatus);
                  if (!config) {
                    setDriveError(pickerDisabledHint(driveStatus));
                    return;
                  }
                  if (editionAdoptionKey.trim() === '') {
                    setDriveError('Enter the edition key (for example "color:1") before choosing its Drive file.');
                    return;
                  }
                  setDriveBusy('pick-edition-file');
                  setDriveError(null);
                  void showDrivePicker(config, 'markdown')
                    .then((picked) => {
                      if (picked) {
                        try {
                          setEditionAdoptionsText((current) =>
                            mergeLibraryAdoptionEntry(current, editionAdoptionKey, picked.id, picked.name),
                          );
                          setDriveNotice(
                            `Granted “${picked.name}” for edition ${editionAdoptionKey.trim()} — press Save destination, then Verify access.`,
                          );
                        } catch (failure) {
                          setDriveError(failure instanceof Error ? failure.message : 'Could not adopt the picked file.');
                        }
                      }
                    })
                    .catch((failure: unknown) => setDriveError(apiErrorMessage(failure)))
                    .finally(() => setDriveBusy(null));
                }}
              >
                {driveBusy === 'pick-edition-file' ? 'Opening Picker…' : 'Choose edition file…'}
              </button>
              <label className="field">
                <span>Team key to grant</span>
                <input
                  type="text"
                  value={teamAdoptionKey}
                  disabled={driveBusy !== null}
                  placeholder="color:red"
                  onChange={(event) => setTeamAdoptionKey(event.target.value)}
                />
              </label>
              <button
                type="button"
                className="ghost-button"
                disabled={driveBusy !== null || pickerConfigFromStatus(driveStatus) === null}
                title={pickerDisabledHint(driveStatus)}
                onClick={() => {
                  const config = pickerConfigFromStatus(driveStatus);
                  if (!config) {
                    setDriveError(pickerDisabledHint(driveStatus));
                    return;
                  }
                  if (teamAdoptionKey.trim() === '') {
                    setDriveError('Enter the team key (for example "color:red") before choosing its Drive file.');
                    return;
                  }
                  setDriveBusy('pick-team-file');
                  setDriveError(null);
                  void showDrivePicker(config, 'markdown')
                    .then((picked) => {
                      if (picked) {
                        try {
                          setTeamAdoptionsText((current) =>
                            mergeLibraryAdoptionEntry(current, teamAdoptionKey, picked.id, picked.name),
                          );
                          setDriveNotice(
                            `Granted “${picked.name}” for team ${teamAdoptionKey.trim()} — press Save destination, then Verify access.`,
                          );
                        } catch (failure) {
                          setDriveError(failure instanceof Error ? failure.message : 'Could not adopt the picked file.');
                        }
                      }
                    })
                    .catch((failure: unknown) => setDriveError(apiErrorMessage(failure)))
                    .finally(() => setDriveBusy(null));
                }}
              >
                {driveBusy === 'pick-team-file' ? 'Opening Picker…' : 'Choose team file…'}
              </button>
              <label className="field">
                <span>Athlete id to grant</span>
                <input
                  type="text"
                  value={athleteAdoptionKey}
                  disabled={driveBusy !== null}
                  placeholder="123"
                  onChange={(event) => setAthleteAdoptionKey(event.target.value)}
                />
              </label>
              <button
                type="button"
                className="ghost-button"
                disabled={driveBusy !== null || pickerConfigFromStatus(driveStatus) === null}
                title={pickerDisabledHint(driveStatus)}
                onClick={() => {
                  const config = pickerConfigFromStatus(driveStatus);
                  if (!config) {
                    setDriveError(pickerDisabledHint(driveStatus));
                    return;
                  }
                  if (athleteAdoptionKey.trim() === '') {
                    setDriveError('Enter the athlete id (for example "123") before choosing its Drive file.');
                    return;
                  }
                  setDriveBusy('pick-athlete-file');
                  setDriveError(null);
                  void showDrivePicker(config, 'markdown')
                    .then((picked) => {
                      if (picked) {
                        try {
                          setAthleteAdoptionsText((current) =>
                            mergeLibraryAdoptionEntry(current, athleteAdoptionKey, picked.id, picked.name),
                          );
                          setDriveNotice(
                            `Granted “${picked.name}” for athlete ${athleteAdoptionKey.trim()} — press Save destination, then Verify access.`,
                          );
                        } catch (failure) {
                          setDriveError(failure instanceof Error ? failure.message : 'Could not adopt the picked file.');
                        }
                      }
                    })
                    .catch((failure: unknown) => setDriveError(apiErrorMessage(failure)))
                    .finally(() => setDriveBusy(null));
                }}
              >
                {driveBusy === 'pick-athlete-file' ? 'Opening Picker…' : 'Choose athlete file…'}
              </button>
            </div>
            <p className="muted small">
              Adopt hand-uploaded library files by semantic key: dossier seasons as <code>1</code>,{' '}
              <code>2</code>; editions as <code>color:1</code>/<code>type:2</code>/<code>chaos:1</code>; teams as{' '}
              <code>color:red</code>/<code>type:Dragon</code>; athletes as stable ids like <code>123</code>.
              Preview the library to discover keys, enter the key above and grant each file with{' '}
              <em>Choose season/edition/team/athlete file…</em> (Google Picker grants drive.file access to
              that exact file), then Save destination. Adopted files update in place under their custom names;
              unknown files are never renamed or deleted.
            </p>
            <p>
              <button
                type="button"
                className="ghost-button"
                disabled={driveBusy !== null}
                onClick={() => {
                  setDriveBusy('save');
                  setDriveError(null);
                  let dossierAdoptions: Record<string, { fileId: string | null; fileName: string | null }> | null = null;
                  let editionAdoptions: Record<string, { fileId: string | null; fileName: string | null }> | null = null;
                  let teamAdoptions: Record<string, { fileId: string | null; fileName: string | null }> | null = null;
                  let athleteAdoptions: Record<string, { fileId: string | null; fileName: string | null }> | null = null;
                  try {
                    dossierAdoptions = parseAdoptionMap(dossierAdoptionsText, 'Dossier adoptions');
                    editionAdoptions = parseAdoptionMap(editionAdoptionsText, 'Edition adoptions');
                    teamAdoptions = parseAdoptionMap(teamAdoptionsText, 'Team adoptions');
                    athleteAdoptions = parseAdoptionMap(athleteAdoptionsText, 'Athlete adoptions');
                  } catch (failure) {
                    setDriveError(failure instanceof Error ? failure.message : 'Invalid adoption JSON.');
                    setDriveBusy(null);
                    return;
                  }
                  void saveDriveDestination(saveId, {
                    mtshistoryFolderId: folderId,
                    mtshistoryFolderName: folderName,
                    seasonsFolderId,
                    dossierFileId,
                    dossierFileName,
                    almanacFileId,
                    almanacFileName,
                    confirmCrossSave,
                    colorCupFolderId,
                    colorCupsFolderId,
                    colorTeamsFolderId,
                    typeCupFolderId,
                    typeCupsFolderId,
                    typeTeamsFolderId,
                    chaosCupFolderId,
                    chaosCupsFolderId,
                    dossierAdoptions,
                    editionAdoptions,
                    teamAdoptions,
                    athletesFolderId,
                    athletesFolderName,
                    athletesIndexFileId,
                    athletesIndexFileName,
                    athleteAdoptions,
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
              scope the app can only use files it created or that you explicitly granted with{' '}
              <em>Choose from Drive</em> (Google Picker, same Google account). After picking or pasting ids, use{' '}
              <em>Verify access</em> to check each grant; each season keeps its own dossier file (
              <code>Seasons/season-N-dossier.md</code>) so a new season never overwrites the prior season&apos;s
              file. Drive <em>Open with</em> needs a public HTTPS app URL and is not available for localhost.
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
              <label className="field">
                <span>
                  <input
                    type="checkbox"
                    checked={allowCreateMissing}
                    disabled={driveBusy !== null}
                    onChange={(event) => setAllowCreateMissing(event.target.checked)}
                  />{' '}
                  Allow creating missing folders/files
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
                    allowCreateMissing,
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
              <button
                type="button"
                className="ghost-button"
                disabled={driveBusy !== null || !driveStatus.connected}
                onClick={() => {
                  setDriveBusy('library-preview');
                  setDriveError(null);
                  setLibraryResult(null);
                  void previewDriveLibrary(saveId, {
                    confirmCrossSave,
                    allowOverwriteEditedRemote: allowOverwrite,
                    allowCreateMissing,
                  })
                    .then((result) => setLibraryResult(result))
                    .catch((failure: unknown) => setDriveError(apiErrorMessage(failure)))
                    .finally(() => setDriveBusy(null));
                }}
              >
                {driveBusy === 'library-preview' ? 'Previewing…' : 'Preview library'}
              </button>
              <button
                type="button"
                className="ghost-button"
                disabled={driveBusy !== null || !driveStatus.connected}
                onClick={() => {
                  setDriveBusy('library-sync');
                  setDriveError(null);
                  setLibraryResult(null);
                  void (async () => {
                    let skip: number | undefined;
                    let aggregated: DriveLibrarySyncResponse | null = null;
                    for (let page = 0; page < 20; page += 1) {
                      const result = await syncDriveLibrary(saveId, {
                        confirmCrossSave,
                        allowOverwriteEditedRemote: allowOverwrite,
                        allowCreateMissing,
                        ...(skip === undefined ? {} : { skip }),
                      });
                      aggregated = aggregated === null ? result : {
                        ...result,
                        totalItems: result.totalItems,
                        truncated: result.truncated,
                        nextSkip: result.nextSkip,
                        dryRun: false,
                        files: [...aggregated.files, ...result.files],
                        categories: mergeLibraryCategories(aggregated.categories, result.categories),
                      };
                      if (!result.truncated || result.nextSkip === null || result.nextSkip === undefined) {
                        if (aggregated !== null) {
                          aggregated = { ...aggregated, truncated: false, nextSkip: null };
                        }
                        break;
                      }
                      skip = result.nextSkip;
                    }
                    if (aggregated !== null) {
                      setLibraryResult(aggregated);
                    }
                    await refreshDriveStatus();
                  })()
                    .catch((failure: unknown) => setDriveError(apiErrorMessage(failure)))
                    .finally(() => setDriveBusy(null));
                }}
              >
                {driveBusy === 'library-sync' ? 'Syncing…' : 'Sync all changed reports'}
              </button>
              <button
                type="button"
                className="ghost-button"
                disabled={driveBusy !== null || !driveStatus.connected}
                onClick={() => {
                  setDriveBusy('athlete-preview');
                  setDriveError(null);
                  setLibraryResult(null);
                  void previewAthleteLibrary(saveId, {
                    confirmCrossSave,
                    allowOverwriteEditedRemote: allowOverwrite,
                    allowCreateMissing,
                  })
                    .then((result) => setLibraryResult(result))
                    .catch((failure: unknown) => setDriveError(apiErrorMessage(failure)))
                    .finally(() => setDriveBusy(null));
                }}
              >
                {driveBusy === 'athlete-preview' ? 'Previewing…' : 'Preview athletes'}
              </button>
              <button
                type="button"
                className="ghost-button"
                disabled={driveBusy !== null || !driveStatus.connected}
                onClick={() => {
                  setDriveBusy('athlete-sync');
                  setDriveError(null);
                  setLibraryResult(null);
                  void (async () => {
                    let skip: number | undefined;
                    let aggregated: DriveLibrarySyncResponse | null = null;
                    for (let page = 0; page < 20; page += 1) {
                      const result = await syncAthleteLibrary(saveId, {
                        confirmCrossSave,
                        allowOverwriteEditedRemote: allowOverwrite,
                        allowCreateMissing,
                        ...(skip === undefined ? {} : { skip }),
                      });
                      aggregated = aggregated === null ? result : {
                        ...result,
                        totalItems: result.totalItems,
                        truncated: result.truncated,
                        nextSkip: result.nextSkip,
                        dryRun: false,
                        files: [...aggregated.files, ...result.files],
                        categories: mergeLibraryCategories(aggregated.categories, result.categories),
                      };
                      if (!result.truncated || result.nextSkip === null || result.nextSkip === undefined) {
                        if (aggregated !== null) {
                          aggregated = { ...aggregated, truncated: false, nextSkip: null };
                        }
                        break;
                      }
                      skip = result.nextSkip;
                    }
                    if (aggregated !== null) {
                      setLibraryResult(aggregated);
                    }
                    await refreshDriveStatus();
                  })()
                    .catch((failure: unknown) => setDriveError(apiErrorMessage(failure)))
                    .finally(() => setDriveBusy(null));
                }}
              >
                {driveBusy === 'athlete-sync' ? 'Syncing…' : 'Publish all athletes / Sync changed athletes'}
              </button>
            </div>
            <p className="muted small">
              Publish now stays dossier + almanac only. The athlete archive (one file per athlete in{' '}
              <code>Athletes/White…Colorless</code> plus <code>athletes-index.md</code>) needs its explicit
              first bulk import above; ordinary runs are incremental. A Drive folder with thousands of reports
              is not ChatGPT Project uploads — use the index with ChatGPT&apos;s Drive picker for explicit
              retrieval.
            </p>
            {driveStatus.athletesFolderId ?? driveStatus.athletesIndexFileId ? (
              <p className="muted small">
                Athletes:{' '}
                {driveStatus.athletesFolderId ? `folder ${driveStatus.athletesFolderId}` : 'folder not adopted'}
                {driveStatus.athletesIndexFileId ? ` · index ${driveStatus.athletesIndexFileId}` : ' · index not published'}
              </p>
            ) : null}
            {driveStatus.lastLibraryPublishedUtc ?? driveStatus.lastLibraryError ? (
              <p className="muted small">
                Library:{' '}
                {driveStatus.lastLibraryPublishedUtc
                  ? `synced ${driveStatus.lastLibraryPublishedUtc}`
                  : 'never fully synced'}
                {` · +${driveStatus.lastLibraryCreated} ~${driveStatus.lastLibraryUpdated} =${driveStatus.lastLibraryUnchanged} !${driveStatus.lastLibraryFailed}`}
                {driveStatus.lastLibraryError ? ` · error: ${driveStatus.lastLibraryError}` : ''}
              </p>
            ) : null}
            {driveStatus.lastPublishedUtc ? (
              <p className="muted small">
                Last publish: season {driveStatus.lastSeasonNumber} · {driveStatus.lastPhase} ·{' '}
                {driveStatus.lastPublishedUtc} · dossier {driveStatus.dossier.lastOutcome ?? '—'} · almanac{' '}
                {driveStatus.almanac.lastOutcome ?? '—'}
                {driveStatus.lastError ? ` · error: ${driveStatus.lastError}` : ''}
              </p>
            ) : null}
            <div className="toolbar" role="group" aria-label="Drive automatic publishing">
              <label className="field">
                <span>
                  <input
                    type="checkbox"
                    checked={driveStatus.autoPublishEnabled}
                    disabled={driveBusy !== null || autoBusy}
                    onChange={(event) => {
                      const enabled = event.target.checked;
                      setAutoBusy(true);
                      setDriveError(null);
                      void setDriveAutoPublish(saveId, enabled)
                        .then(() => refreshDriveStatus())
                        .catch((failure: unknown) => setDriveError(apiErrorMessage(failure)))
                        .finally(() => setAutoBusy(false));
                    }}
                  />{' '}
                  Automatically publish after each completed stage
                </span>
              </label>
              <span className="muted small" aria-live="polite">
                Auto-publish: {describeAutoPublishStatus(driveStatus.autoPublishStatus)}
                {driveStatus.autoRequestedSeason !== null
                  ? ` · requested season ${driveStatus.autoRequestedSeason} · stage ${driveStatus.autoRequestedGlobalStage ?? '—'}`
                  : ''}
              </span>
              <button
                type="button"
                className="ghost-button"
                disabled={driveBusy !== null || autoBusy || !driveStatus.autoPublishEnabled}
                onClick={() => {
                  setAutoBusy(true);
                  setDriveError(null);
                  void retryDriveAutoPublish(saveId)
                    .then(() => refreshDriveStatus())
                    .catch((failure: unknown) => setDriveError(apiErrorMessage(failure)))
                    .finally(() => setAutoBusy(false));
                }}
              >
                {autoBusy ? 'Working…' : 'Retry auto-publish'}
              </button>
            </div>
            {driveStatus.autoLastError ? (
              <p className="muted small">Auto-publish note: {driveStatus.autoLastError}</p>
            ) : null}
            {driveStatus.autoPublishStatus === 'Disconnected' && driveStatus.autoPublishEnabled ? (
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
            {libraryResult ? (
              <ul className="muted small">
                <li>
                  Library {libraryResult.dryRun ? 'preview' : 'sync'}: {libraryResult.totalItems} items
                  {libraryResult.truncated ? ' (truncated — re-run to resume the remainder)' : ''}
                </li>
                {libraryResult.categories.map((category) => (
                  <li key={category.category}>
                    {category.category}: {category.total} total · +{category.created} ~{category.updated} =
                    {category.unchanged} !{category.failed}
                  </li>
                ))}
                {libraryResult.files
                  .filter((file) => file.outcome === 'failed')
                  .map((file) => (
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
