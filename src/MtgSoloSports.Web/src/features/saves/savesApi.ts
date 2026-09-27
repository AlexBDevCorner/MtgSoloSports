import { ApiError, fetchJson } from '../../shared/api/http';

export interface SaveSummary {
  saveId: string;
  name: string;
  createdUtc: string;
  schemaVersion: number;
  currentSeason: number;
  phase: string;
}

export interface SaveListResponse {
  saves: SaveSummary[];
}

export interface SaveDetail {
  saveId: string;
  name: string;
  createdUtc: string;
  schemaVersion: number;
  currentSeason: number;
  phase: string;
  rngAlgorithm: string;
  rngVersion: number;
  rngState: string;
  rngStream: string;
  rulesVersion: number;
}

export interface CreateSaveResult {
  saveId: string;
  name: string;
  createdUtc: string;
  schemaVersion: number;
  currentSeason: number;
  phase: string;
  totalAthletes: number;
  athletesPerColor: Record<string, number>;
  universeChecksum: string;
}

export async function listSaves(signal?: AbortSignal): Promise<SaveSummary[]> {
  const response = await fetchJson<SaveListResponse>('/api/saves', { signal });
  return [...response.saves].sort((a, b) =>
    a.createdUtc < b.createdUtc ? 1 : a.createdUtc > b.createdUtc ? -1 : 0,
  );
}

export async function openSave(saveId: string, signal?: AbortSignal): Promise<SaveDetail> {
  return fetchJson<SaveDetail>(`/api/saves/${saveId}`, { signal });
}

export async function createSave(
  name: string,
  seed?: number,
  stream?: number,
): Promise<CreateSaveResult> {
  return fetchJson<CreateSaveResult>('/api/saves', {
    method: 'POST',
    body: JSON.stringify({
      name,
      ...(seed === undefined ? {} : { seed }),
      ...(stream === undefined ? {} : { stream }),
    }),
  });
}

export async function deleteSave(saveId: string): Promise<void> {
  const response = await fetch(`/api/saves/${saveId}`, { method: 'DELETE' });
  if (!response.ok) {
    const body = await response.text().catch(() => '');
    throw new Error(body || `Delete failed (${response.status}).`);
  }
}

export function exportSaveUrl(saveId: string): string {
  return `/api/saves/${saveId}/export`;
}

export async function importSave(file: Blob, overwrite: boolean): Promise<SaveDetail> {
  const response = await fetch(`/api/saves/import${overwrite ? '?overwrite=true' : ''}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/zip' },
    body: file,
  });
  if (!response.ok) {
    const body = await response.text().catch(() => '');
    throw new ApiError(response.status, body || response.statusText);
  }
  return (await response.json()) as SaveDetail;
}
