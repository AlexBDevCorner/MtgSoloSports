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
  };
  return fetchJson<DriveDestination>(driveDestinationUrl(saveId), {
    method: 'PUT',
    body: JSON.stringify(body),
  });
}

export function publishDriveReports(
  saveId: string,
  options: { confirmCrossSave: boolean; allowOverwriteEditedRemote: boolean },
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
