import { qualifierBoundaryLabel, type QualifierBoundary } from '../../shared/leagueTiers.ts';
import type { QualifierEvent, QualifierEventMember, QualifierList } from './qualifierApi.ts';

/**
 * Pure presentation model for the multi-qualifier postseason phase
 * (MSS-060). The backend stays authoritative for fields and outcomes: this
 * slice only orders the 17 ordinary-postseason qualifiers canonically
 * (Superleague, then F1↔F2 by color, then F2↔F3 by color), derives
 * completed/current/pending state from persisted facts, and shapes the
 * 32-athlete and 16-athlete fields identically. DOM-free so unit-testable.
 */

export type QualifierStatus = 'complete' | 'pending';

export interface QualifierEntry {
  /** Stable identity for React keys, routes and aria references. */
  key: string;
  boundary: string;
  sportingColor: number | null;
  sportingColorName: string;
  title: string;
  /** Canonical order: Superleague first, then F1↔F2, then F2↔F3, colors 0..7. */
  order: number;
  status: QualifierStatus;
  qualifierSize: number | null;
  roundCount: number | null;
  winners: number | null;
  qualifiedCount: number | null;
  event: QualifierEvent | null;
}

function boundaryOrder(boundary: string): number {
  if (boundary === 'Feeder1Feeder2') {
    return 1;
  }
  if (boundary === 'Feeder2Feeder3') {
    return 2;
  }
  return 0;
}

function colorOrder(color: number | null): number {
  return color === null || color === undefined ? -1 : color;
}

function entryKey(boundary: string, color: number | null): string {
  return `${boundary}:${color === null || color === undefined ? '-' : color}`;
}

/** Canonical qualifier list: all eight sporting colors per feeder boundary. */
export function canonicalQualifierEntries(includeFeeders: boolean): QualifierEntry[] {
  const entries: QualifierEntry[] = [
    {
      key: entryKey('Superleague', null),
      boundary: 'Superleague',
      sportingColor: null,
      sportingColorName: '-',
      title: qualifierBoundaryLabel('Superleague', null),
      order: -1,
      status: 'pending',
      qualifierSize: null,
      roundCount: null,
      winners: null,
      qualifiedCount: null,
      event: null,
    },
  ];
  if (!includeFeeders) {
    return entries;
  }
  const boundaries: QualifierBoundary[] = ['Feeder1Feeder2', 'Feeder2Feeder3'];
  for (const boundary of boundaries) {
    for (let color = 0; color < 8; color += 1) {
      entries.push({
        key: entryKey(boundary, color),
        boundary,
        sportingColor: color,
        sportingColorName: String(color),
        title: qualifierBoundaryLabel(boundary, `Color ${color}`),
        order: boundaryOrder(boundary) * 8 + color,
        status: 'pending',
        qualifierSize: null,
        roundCount: null,
        winners: null,
        qualifiedCount: null,
        event: null,
      });
    }
  }
  return entries;
}

function toEntry(event: QualifierEvent): QualifierEntry {
  const qualified = event.standings.filter((member) => member.isQualified).length;
  return {
    key: entryKey(event.boundary, event.sportingColor),
    boundary: event.boundary,
    sportingColor: event.sportingColor,
    sportingColorName: event.sportingColorName,
    title: qualifierBoundaryLabel(event.boundary, event.sportingColorName),
    order: boundaryOrder(event.boundary) * 8 + colorOrder(event.sportingColor),
    status: 'complete',
    qualifierSize: event.qualifierSize,
    roundCount: event.roundCount,
    winners: event.winners,
    qualifiedCount: qualified,
    event,
  };
}

/**
 * Merges persisted qualifier results over the canonical list so
 * completed/pending state stays visible even mid-phase. `includeFeeders`
 * comes from league data (tiered saves expect 17 events, v1 saves expect 1)
 * so v1 overviews never show phantom feeder qualifiers. Unknown
 * (non-canonical) events are appended, never dropped.
 */
export function buildQualifierOverview(
  list: QualifierList | null,
  includeFeeders: boolean,
): QualifierEntry[] {
  const canonical = canonicalQualifierEntries(includeFeeders);
  if (!list) {
    return canonical;
  }
  const byKey = new Map(canonical.map((entry) => [entry.key, entry]));
  for (const event of list.events) {
    byKey.set(entryKey(event.boundary, event.sportingColor), toEntry(event));
  }
  return [...byKey.values()].sort((a, b) => a.order - b.order);
}

export interface QualifierFieldRow {
  athleteId: number;
  name: string;
  sportingColor: string;
  role: string;
  fromLeagueId: number;
  fromLeagueName: string;
  fromSeasonRank: number;
  qualifierRank: number;
  qualifierScoreThousandths: number;
  baseScoreThousandths: number;
  roundWins: number;
  isQualified: boolean;
}

function toRow(member: QualifierEventMember): QualifierFieldRow {
  return { ...member };
}

/** Final field ordered by qualifier rank (top-8 cutoff first). */
export function qualifierFieldRows(event: QualifierEvent): QualifierFieldRow[] {
  return [...event.standings].map(toRow).sort((a, b) => a.qualifierRank - b.qualifierRank);
}

export function qualifierIncumbents(rows: readonly QualifierFieldRow[]): QualifierFieldRow[] {
  return rows.filter((row) => row.role === 'Incumbent');
}

export function qualifierChallengers(rows: readonly QualifierFieldRow[]): QualifierFieldRow[] {
  return rows.filter((row) => row.role === 'Challenger');
}

/** "12/16 complete · top 8 qualify": compact progress from persisted facts. */
export function qualifierProgressLine(entry: QualifierEntry): string {
  if (entry.status !== 'complete' || entry.event === null) {
    return 'Pending — run to reveal the field and outcome.';
  }
  const size = entry.qualifierSize ?? entry.event.standings.length;
  const winners = entry.winners ?? entry.qualifiedCount ?? 8;
  return `${size} athletes · ${entry.event.roundCount} rounds · top ${winners} qualify · ${entry.qualifiedCount ?? winners}/${size} qualified.`;
}

/** "QUALIFIED" / "ELIMINATED": text labels, never color alone. */
export function qualifierOutcomeLabel(row: QualifierFieldRow): string {
  return row.isQualified ? 'QUALIFIED' : 'ELIMINATED';
}

export function qualifierRoleLabel(role: string): string {
  return role === 'Incumbent' ? 'Incumbent' : role === 'Challenger' ? 'Challenger' : role;
}
