import { ApiError } from '../../shared/api/http';

export type ReportFormat = 'json' | 'md';

export function athleteReportUrl(saveId: string, athleteId: number, format: ReportFormat): string {
  return `/api/saves/${saveId}/athletes/${athleteId}/report?format=${format}`;
}

export function standingsReportUrl(
  saveId: string,
  leagueId: number,
  seasonNumber: number,
  format: ReportFormat,
): string {
  return `/api/saves/${saveId}/leagues/${leagueId}/seasons/${seasonNumber}/report?format=${format}`;
}

export type CupReportKind = 'color' | 'type';

export function cupEditionReportUrl(
  saveId: string,
  cup: CupReportKind,
  seasonNumber: number,
  format: ReportFormat,
): string {
  return `/api/saves/${saveId}/cups/${cup}/editions/${seasonNumber}/report?format=${format}`;
}

export function cupTeamReportUrl(
  saveId: string,
  cup: CupReportKind,
  teamKey: string,
  format: ReportFormat,
): string {
  return `/api/saves/${saveId}/cups/${cup}/teams/${encodeURIComponent(teamKey)}/report?format=${format}`;
}

export function leagueProfileReportUrl(saveId: string, leagueKey: string, format: ReportFormat): string {
  return `/api/saves/${saveId}/leagues/${encodeURIComponent(leagueKey)}/report?format=${format}`;
}

export function seasonDossierReportUrl(saveId: string, seasonNumber: number, format: ReportFormat): string {
  return `/api/saves/${saveId}/reports/season/${seasonNumber}?format=${format}`;
}

export function universeAlmanacReportUrl(saveId: string, format: ReportFormat): string {
  return `/api/saves/${saveId}/reports/almanac?format=${format}`;
}

function filenameFromDisposition(header: string | null, fallback: string): string {
  if (!header) {
    return fallback;
  }
  const star = header.match(/filename\*=UTF-8''([^;]+)/i);
  if (star?.[1]) {
    try {
      return decodeURIComponent(star[1].trim().replace(/^"|"$/g, ''));
    } catch {
      return star[1];
    }
  }
  const quoted = header.match(/filename="?([^";]+)"?/i);
  if (quoted?.[1]) {
    return quoted[1].trim();
  }
  return fallback;
}

/**
 * Downloads a report endpoint response as a file. Read-only: GET only, never
 * mutates saves. Parses server error JSON for visible feedback.
 */
export async function downloadReport(url: string, fallbackFilename: string): Promise<void> {
  const response = await fetch(url);
  if (!response.ok) {
    const body = await response.text().catch(() => '');
    let message = `Download failed (${response.status}).`;
    try {
      const parsed = JSON.parse(body) as { error?: unknown };
      if (typeof parsed.error === 'string' && parsed.error.length > 0) {
        message = parsed.error;
      }
    } catch {
      if (body.length > 0 && body.length < 300) {
        message = body;
      }
    }
    throw new ApiError(response.status, message);
  }
  const blob = await response.blob();
  const filename = filenameFromDisposition(response.headers.get('content-disposition'), fallbackFilename);
  const objectUrl = URL.createObjectURL(blob);
  try {
    const anchor = document.createElement('a');
    anchor.href = objectUrl;
    anchor.download = filename;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
  } finally {
    // Revoke on the next tick so the download has started.
    setTimeout(() => URL.revokeObjectURL(objectUrl), 0);
  }
}
