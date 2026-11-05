import { fetchJson } from '../../shared/api/http';

export interface DriveReportSlot {
  fileId: string | null;
  fileName: string | null;
  fingerprint: string | null;
  remoteModifiedTime: string | null;
  lastOutcome: string | null;
  webViewLink: string | null;
}

export interface DriveStatus {
  saveId: string;
  saveName: string;
  currentSeason: number;
  phase: string;
  configured: boolean;
  connected: boolean;
  scopes: string | null;
  connectedUtc: string | null;
  dossier: DriveReportSlot;
  almanac: DriveReportSlot;
  mtshistoryFolderId: string | null;
  mtshistoryFolderName: string | null;
  seasonsFolderId: string | null;
  lastPublishedUtc: string | null;
  lastSeasonNumber: number | null;
  lastPhase: string | null;
  lastGlobalStage: number | null;
  lastError: string | null;
  pickerClientId: string | null;
  pickerApiKey: string | null;
  pickerAppId: string | null;
  pickerEnabled: boolean;
  autoPublishEnabled: boolean;
  autoPublishStatus: string;
  autoRequestedSeason: number | null;
  autoRequestedGlobalStage: number | null;
  autoRequestedPhase: string | null;
  autoRequestedUtc: string | null;
  autoAttemptCount: number;
  autoNextRetryUtc: string | null;
  autoLastError: string | null;
  autoPausedForAuth: boolean;
  colorCupFolderId: string | null;
  colorCupsFolderId: string | null;
  colorTeamsFolderId: string | null;
  typeCupFolderId: string | null;
  typeCupsFolderId: string | null;
  typeTeamsFolderId: string | null;
  chaosCupFolderId: string | null;
  chaosCupsFolderId: string | null;
  lastLibraryPublishedUtc: string | null;
  lastLibraryError: string | null;
  lastLibraryCreated: number;
  lastLibraryUpdated: number;
  lastLibraryUnchanged: number;
  lastLibraryFailed: number;
  lastLibraryByCategory: Record<string, { created: number; updated: number; unchanged: number; failed: number }> | null;
  athletesFolderId: string | null;
  athletesFolderName: string | null;
  athleteColorFolderIds: Record<string, string> | null;
  athletesIndexFileId: string | null;
  athletesIndexFileName: string | null;
}

export interface DriveDestination {
  saveId: string;
  saveName: string;
  mtshistoryFolderId: string | null;
  mtshistoryFolderName: string | null;
  seasonsFolderId: string | null;
  dossierFileId: string | null;
  dossierFileName: string | null;
  almanacFileId: string | null;
  almanacFileName: string | null;
  dossierFingerprint: string | null;
  almanacFingerprint: string | null;
  lastPublishedUtc: string | null;
  lastSeasonNumber: number | null;
  lastPhase: string | null;
  lastGlobalStage: number | null;
  lastError: string | null;
  autoPublishEnabled: boolean;
  autoPublishStatus: string;
  autoRequestedSeason: number | null;
  autoRequestedGlobalStage: number | null;
  autoRequestedPhase: string | null;
  colorCupFolderId: string | null;
  colorCupsFolderId: string | null;
  colorTeamsFolderId: string | null;
  typeCupFolderId: string | null;
  typeCupsFolderId: string | null;
  typeTeamsFolderId: string | null;
  chaosCupFolderId: string | null;
  chaosCupsFolderId: string | null;
  lastLibraryPublishedUtc: string | null;
  lastLibraryError: string | null;
  lastLibraryCreated: number;
  lastLibraryUpdated: number;
  lastLibraryUnchanged: number;
  lastLibraryFailed: number;
  lastLibraryByCategory: Record<string, { created: number; updated: number; unchanged: number; failed: number }> | null;
  athletesFolderId: string | null;
  athletesFolderName: string | null;
  athleteColorFolderIds: Record<string, string> | null;
  athletesIndexFileId: string | null;
  athletesIndexFileName: string | null;
}

export interface DriveLibraryAdoptionInput {
  fileId: string | null;
  fileName: string | null;
}

export interface DriveDestinationInput {
  mtshistoryFolderId: string;
  mtshistoryFolderName: string;
  seasonsFolderId: string;
  dossierFileId: string;
  dossierFileName: string;
  almanacFileId: string;
  almanacFileName: string;
  confirmCrossSave: boolean;
  colorCupFolderId: string;
  colorCupsFolderId: string;
  colorTeamsFolderId: string;
  typeCupFolderId: string;
  typeCupsFolderId: string;
  typeTeamsFolderId: string;
  chaosCupFolderId: string;
  chaosCupsFolderId: string;
  dossierAdoptions?: Record<string, DriveLibraryAdoptionInput> | null;
  editionAdoptions?: Record<string, DriveLibraryAdoptionInput> | null;
  teamAdoptions?: Record<string, DriveLibraryAdoptionInput> | null;
  athletesFolderId: string;
  athletesFolderName: string;
  athleteColorFolderIds?: Record<string, string> | null;
  athleteAdoptions?: Record<string, DriveLibraryAdoptionInput> | null;
  athletesIndexFileId: string;
  athletesIndexFileName: string;
}

export interface DriveVerifyCheck {
  report: string;
  status: string;
  fileId: string | null;
  fileName: string | null;
  detail: string | null;
}

export interface DriveVerifyResponse {
  saveId: string;
  saveName: string;
  currentSeason: number;
  checks: DriveVerifyCheck[];
}

export interface DriveFileOutcome {
  report: string;
  outcome: 'created' | 'updated' | 'unchanged' | 'failed';
  fileId: string | null;
  fileName: string | null;
  fingerprint: string | null;
  webViewLink: string | null;
  detail: string | null;
}

export interface PublishDriveResponse {
  saveId: string;
  seasonNumber: number;
  phase: string;
  globalStage: number | null;
  publishedUtc: string;
  files: DriveFileOutcome[];
}

export function driveStatusUrl(saveId: string): string {
  return `/api/saves/${saveId}/reports/drive/status`;
}

export function driveAuthUrl(saveId: string): string {
  return `/api/saves/${saveId}/reports/drive/auth-url`;
}

export function driveDisconnectUrl(saveId: string): string {
  return `/api/saves/${saveId}/reports/drive/disconnect`;
}

export function driveDestinationUrl(saveId: string): string {
  return `/api/saves/${saveId}/reports/drive/destination`;
}

export function drivePublishUrl(saveId: string): string {
  return `/api/saves/${saveId}/reports/drive/publish`;
}

export function driveVerifyUrl(saveId: string): string {
  return `/api/saves/${saveId}/reports/drive/verify`;
}

export function driveAutoPublishUrl(saveId: string): string {
  return `/api/saves/${saveId}/reports/drive/auto-publish`;
}

export function driveAutoPublishRetryUrl(saveId: string): string {
  return `/api/saves/${saveId}/reports/drive/auto-publish/retry`;
}

export function getDriveStatus(saveId: string, signal?: AbortSignal): Promise<DriveStatus> {
  return fetchJson<DriveStatus>(driveStatusUrl(saveId), { signal });
}

export function getDriveDestination(saveId: string, signal?: AbortSignal): Promise<DriveDestination> {
  return fetchJson<DriveDestination>(driveDestinationUrl(saveId), { signal });
}

export async function startDriveConnect(saveId: string): Promise<void> {
  const result = await fetchJson<{ authUrl: string }>(driveAuthUrl(saveId));
  window.location.href = result.authUrl;
}

export async function disconnectDrive(saveId: string): Promise<{ connected: boolean }> {
  return fetchJson<{ connected: boolean }>(driveDisconnectUrl(saveId), { method: 'POST' });
}

export function saveDriveDestination(saveId: string, input: DriveDestinationInput): Promise<DriveDestination> {
  const body = {
    mtshistoryFolderId: input.mtshistoryFolderId.trim() === '' ? null : input.mtshistoryFolderId.trim(),
    mtshistoryFolderName: input.mtshistoryFolderName.trim() === '' ? null : input.mtshistoryFolderName.trim(),
    seasonsFolderId: input.seasonsFolderId.trim() === '' ? null : input.seasonsFolderId.trim(),
    dossierFileId: input.dossierFileId.trim() === '' ? null : input.dossierFileId.trim(),
    dossierFileName: input.dossierFileName.trim() === '' ? null : input.dossierFileName.trim(),
    almanacFileId: input.almanacFileId.trim() === '' ? null : input.almanacFileId.trim(),
    almanacFileName: input.almanacFileName.trim() === '' ? null : input.almanacFileName.trim(),
    confirmCrossSave: input.confirmCrossSave,
    colorCupFolderId: input.colorCupFolderId.trim() === '' ? null : input.colorCupFolderId.trim(),
    colorCupsFolderId: input.colorCupsFolderId.trim() === '' ? null : input.colorCupsFolderId.trim(),
    colorTeamsFolderId: input.colorTeamsFolderId.trim() === '' ? null : input.colorTeamsFolderId.trim(),
    typeCupFolderId: input.typeCupFolderId.trim() === '' ? null : input.typeCupFolderId.trim(),
    typeCupsFolderId: input.typeCupsFolderId.trim() === '' ? null : input.typeCupsFolderId.trim(),
    typeTeamsFolderId: input.typeTeamsFolderId.trim() === '' ? null : input.typeTeamsFolderId.trim(),
    chaosCupFolderId: input.chaosCupFolderId.trim() === '' ? null : input.chaosCupFolderId.trim(),
    chaosCupsFolderId: input.chaosCupsFolderId.trim() === '' ? null : input.chaosCupsFolderId.trim(),
    dossierAdoptions: input.dossierAdoptions ?? null,
    editionAdoptions: input.editionAdoptions ?? null,
    teamAdoptions: input.teamAdoptions ?? null,
    athletesFolderId: input.athletesFolderId.trim() === '' ? null : input.athletesFolderId.trim(),
    athletesFolderName: input.athletesFolderName.trim() === '' ? null : input.athletesFolderName.trim(),
    athleteColorFolderIds: input.athleteColorFolderIds ?? null,
    athleteAdoptions: input.athleteAdoptions ?? null,
    athletesIndexFileId: input.athletesIndexFileId.trim() === '' ? null : input.athletesIndexFileId.trim(),
    athletesIndexFileName: input.athletesIndexFileName.trim() === '' ? null : input.athletesIndexFileName.trim(),
  };
  return fetchJson<DriveDestination>(driveDestinationUrl(saveId), {
    method: 'PUT',
    body: JSON.stringify(body),
  });
}

export interface DriveLibraryCategoryResult {
  category: string;
  total: number;
  created: number;
  updated: number;
  unchanged: number;
  failed: number;
}

export interface DriveLibrarySyncResponse {
  saveId: string;
  dryRun: boolean;
  truncated: boolean;
  totalItems: number;
  nextSkip: number | null;
  syncedUtc: string;
  categories: DriveLibraryCategoryResult[];
  files: DriveFileOutcome[];
}

export function driveLibraryPreviewUrl(saveId: string): string {
  return `/api/saves/${saveId}/reports/drive/library/preview`;
}

export function driveLibrarySyncUrl(saveId: string): string {
  return `/api/saves/${saveId}/reports/drive/library/sync`;
}

export function previewDriveLibrary(
  saveId: string,
  options: {
    confirmCrossSave: boolean;
    allowOverwriteEditedRemote: boolean;
    allowCreateMissing?: boolean;
    skip?: number;
    maxFiles?: number;
    categories?: string[] | null;
    onlyKeys?: string[] | null;
  },
): Promise<DriveLibrarySyncResponse> {
  return fetchJson<DriveLibrarySyncResponse>(driveLibraryPreviewUrl(saveId), {
    method: 'POST',
    body: JSON.stringify(options),
  });
}

export function syncDriveLibrary(
  saveId: string,
  options: {
    confirmCrossSave: boolean;
    allowOverwriteEditedRemote: boolean;
    allowCreateMissing?: boolean;
    skip?: number;
    maxFiles?: number;
    categories?: string[] | null;
    onlyKeys?: string[] | null;
  },
): Promise<DriveLibrarySyncResponse> {
  return fetchJson<DriveLibrarySyncResponse>(driveLibrarySyncUrl(saveId), {
    method: 'POST',
    body: JSON.stringify(options),
  });
}

export const athleteLibraryCategories = ['athlete', 'athletes-index'];

export function previewAthleteLibrary(
  saveId: string,
  options: {
    confirmCrossSave: boolean;
    allowOverwriteEditedRemote: boolean;
    allowCreateMissing?: boolean;
    skip?: number;
    maxFiles?: number;
  },
): Promise<DriveLibrarySyncResponse> {
  return previewDriveLibrary(saveId, { ...options, categories: athleteLibraryCategories });
}

export function syncAthleteLibrary(
  saveId: string,
  options: {
    confirmCrossSave: boolean;
    allowOverwriteEditedRemote: boolean;
    allowCreateMissing?: boolean;
    skip?: number;
    maxFiles?: number;
  },
): Promise<DriveLibrarySyncResponse> {
  return syncDriveLibrary(saveId, { ...options, categories: athleteLibraryCategories });
}

export function publishDriveReports(
  saveId: string,
  options: { confirmCrossSave: boolean; allowOverwriteEditedRemote: boolean; allowCreateMissing?: boolean },
): Promise<PublishDriveResponse> {
  return fetchJson<PublishDriveResponse>(drivePublishUrl(saveId), {
    method: 'POST',
    body: JSON.stringify(options),
  });
}

export function verifyDriveDestination(saveId: string): Promise<DriveVerifyResponse> {
  return fetchJson<DriveVerifyResponse>(driveVerifyUrl(saveId), {
    method: 'POST',
  });
}

export function setDriveAutoPublish(saveId: string, enabled: boolean): Promise<DriveDestination> {
  return fetchJson<DriveDestination>(driveAutoPublishUrl(saveId), {
    method: 'PUT',
    body: JSON.stringify({ enabled }),
  });
}

export function retryDriveAutoPublish(saveId: string): Promise<DriveDestination> {
  return fetchJson<DriveDestination>(driveAutoPublishRetryUrl(saveId), {
    method: 'POST',
  });
}

export function describeAutoPublishStatus(status: string): string {
  switch (status) {
    case 'UpToDate':
      return 'Up to date';
    case 'Publishing':
      return 'Publishing';
    case 'Pending':
      return 'Pending';
    case 'Failed':
      return 'Failed';
    case 'Disconnected':
      return 'Disconnected';
    case 'Disabled':
    default:
      return 'Disabled';
  }
}

/**
 * MSS-091 correction: pure helper that merges one Google Picker file grant
 * into a library adoption JSON map (dossier/edition/team). The Picker grant
 * is what makes a hand-uploaded file visible under the least-privilege
 * drive.file scope; pasting an id alone does not grant access. Returns the
 * updated JSON text so the caller can Save destination + Verify access.
 * Throws on an invalid key or unparseable existing JSON.
 */
export function mergeLibraryAdoptionEntry(
  raw: string,
  key: string,
  fileId: string,
  fileName: string | null,
): string {
  const trimmedKey = key.trim();
  if (trimmedKey === '' || trimmedKey.includes('/') || trimmedKey.includes('\\')) {
    throw new Error('Adoption key is invalid. Use "1" for dossiers, "color:1"/"type:2"/"chaos:1" for editions, "color:red"/"type:Dragon" for teams, athlete id like "123" for athletes.');
  }
  const trimmedId = fileId.trim();
  if (trimmedId === '') {
    throw new Error('Picked file id is empty.');
  }
  const trimmedName = typeof fileName === 'string' && fileName.trim() !== '' ? fileName.trim() : null;
  let current: Record<string, DriveLibraryAdoptionInput> = {};
  const rawTrimmed = raw.trim();
  if (rawTrimmed !== '') {
    let parsed: unknown;
    try {
      parsed = JSON.parse(rawTrimmed);
    } catch {
      throw new Error('Existing adoptions are not valid JSON. Fix the JSON before picking another file.');
    }
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
      throw new Error('Existing adoptions must be a JSON object keyed by semantic report key.');
    }
    const entries = Object.entries(parsed as Record<string, unknown>);
    if (entries.length >= 500 && !(trimmedKey in (parsed as Record<string, unknown>))) {
      throw new Error('Adoption map has too many entries (max 500).');
    }
    for (const [entryKey, value] of entries) {
      if (typeof value !== 'object' || value === null) {
        throw new Error(`Existing adoption entry "${entryKey}" must be {"fileId": "...", "fileName": "..."}.`);
      }
      const record = value as Record<string, unknown>;
      const existingId = record.fileId;
      const existingName = record.fileName;
      current[entryKey] = {
        fileId: typeof existingId === 'string' && existingId.trim() !== '' ? existingId.trim() : null,
        fileName: typeof existingName === 'string' && existingName.trim() !== '' ? existingName.trim() : null,
      };
    }
  }
  current[trimmedKey] = { fileId: trimmedId, fileName: trimmedName };
  return JSON.stringify(current);
}
