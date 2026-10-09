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

/**
 * Source tier for a qualifier role, from the boundary's persisted bands
 * (incumbents defend from the upper tier, challengers attack from the lower).
 */
export function qualifierRoleTier(role: string, boundary: string): string | null {
  const upper =
    boundary === 'Superleague' ? 'Superleague' : boundary === 'Feeder1Feeder2' ? 'Feeder 1' : boundary === 'Feeder2Feeder3' ? 'Feeder 2' : null;
  const lower =
    boundary === 'Superleague' ? 'Feeder 1' : boundary === 'Feeder1Feeder2' ? 'Feeder 2' : boundary === 'Feeder2Feeder3' ? 'Feeder 3' : null;
  if (role === 'Incumbent') {
    return upper;
  }
  if (role === 'Challenger') {
    return lower;
  }
  return null;
}

export interface QualifierOutcomeGroup {
  key: string;
  boundary: string;
  title: string;
  sportingColorName: string;
  /** Destination tier the winners take places in. */
  destination: string;
  qualified: Array<{ athleteId: number; name: string; role: string; fromSeasonRank: number }>;
  eliminatedCount: number;
}

/** Sporting colors in backend enum order (canonical qualifier order). */
export const QUALIFIER_COLOR_ORDER = [
  'White',
  'Blue',
  'Black',
  'Red',
  'Green',
  'Multicolor',
  'Hybrid',
  'Colorless',
] as const;

/**
 * Live `?qualifier=` param for one event: `superleague` or
 * `f1f2-<color>` / `f2f3-<color>` (color lower-case). Stable, copyable and
 * distinct per season + identity + round (round lives in `?round=`).
 */
export function qualifierLiveParam(boundary: string, colorName: string | null): string {
  if (boundary === 'Feeder1Feeder2') {
    return `f1f2-${(colorName ?? '').toLowerCase()}`;
  }
  if (boundary === 'Feeder2Feeder3') {
    return `f2f3-${(colorName ?? '').toLowerCase()}`;
  }
  return 'superleague';
}

/** Parses a Live `?qualifier=` param back to boundary + color name. */
export function parseQualifierLiveParam(value: string | null | undefined): {
  boundary: 'Superleague' | 'Feeder1Feeder2' | 'Feeder2Feeder3';
  color: string | null;
} {
  const normalized = (value ?? '').trim().toLowerCase();
  if (!normalized || normalized === 'superleague' || normalized === 'sl') {
    return { boundary: 'Superleague', color: null };
  }
  const match = /^f(1f2|2f3)-([a-z]+)$/.exec(normalized);
  if (match) {
    const boundary = match[1] === '1f2' ? 'Feeder1Feeder2' : 'Feeder2Feeder3';
    const found = QUALIFIER_COLOR_ORDER.find((name) => name.toLowerCase() === match[2]);
    if (found) {
      return { boundary, color: found };
    }
  }
  return { boundary: 'Superleague', color: null };
}

/** Canonical 17-event Live order: Superleague, F1↔F2 colors, F2↔F3 colors. */
export function canonicalQualifierLiveParams(): string[] {
  const params = ['superleague'];
  for (const color of QUALIFIER_COLOR_ORDER) {
    params.push(`f1f2-${color.toLowerCase()}`);
  }
  for (const color of QUALIFIER_COLOR_ORDER) {
    params.push(`f2f3-${color.toLowerCase()}`);
  }
  return params;
}

/** Next canonical qualifier Live param after `current`, or null when done. */
export function nextQualifierLiveParam(current: string | null | undefined): string | null {
  const order = canonicalQualifierLiveParams();
  const normalized = (current ?? '').trim().toLowerCase() || 'superleague';
  const index = order.indexOf(normalized);
  if (index < 0 || index + 1 >= order.length) {
    return null;
  }
  return order[index + 1]!;
}

/** API boundary slug (`F1F2`/`F2F3`) for a feeder Live param. */
export function qualifierApiBoundary(param: string): string {
  if (param.startsWith('f2f3-')) {
    return 'F2F3';
  }
  return 'F1F2';
}

/** Color name (`White`, …) for a feeder Live param. */
export function qualifierApiColor(param: string): string {
  const dash = param.indexOf('-');
  const slug = dash >= 0 ? param.slice(dash + 1) : '';
  const found = QUALIFIER_COLOR_ORDER.find((name) => name.toLowerCase() === slug.toLowerCase());
  return found ?? slug;
}

/** Destination tier for qualifier winners, from data (boundary identity). */
export function qualifierDestination(boundary: string): string {
  if (boundary === 'Feeder1Feeder2') {
    return 'Feeder 1';
  }
  if (boundary === 'Feeder2Feeder3') {
    return 'Feeder 2';
  }
  return 'Superleague';
}

/**
 * Qualifier outcomes per boundary/color for the movement reveal (MSS-060):
 * who qualified (top-8 cutoff from data) and which tier they take places in.
 * Automatic movement comes from the movement endpoints; this is the
 * qualifier-decided counterpart, so the reveal can distinguish the two
 * without resimulating anything.
 */
export function qualifierOutcomeGroups(list: QualifierList | null): QualifierOutcomeGroup[] {
  if (!list) {
    return [];
  }
  return [...list.events]
    .sort(
      (a, b) =>
        boundaryOrder(a.boundary) * 8 + colorOrder(a.sportingColor) - (boundaryOrder(b.boundary) * 8 + colorOrder(b.sportingColor)),
    )
    .map((event) => ({
      key: `${event.boundary}:${event.sportingColor ?? '-'}`,
      boundary: event.boundary,
      title: qualifierBoundaryLabel(event.boundary, event.sportingColorName),
      sportingColorName: event.sportingColorName,
      destination: qualifierDestination(event.boundary),
      qualified: [...event.standings]
        .filter((member) => member.isQualified)
        .sort((a, b) => a.qualifierRank - b.qualifierRank)
        .map((member) => ({
          athleteId: member.athleteId,
          name: member.name,
          role: member.role,
          fromSeasonRank: member.fromSeasonRank,
        })),
      eliminatedCount: event.standings.filter((member) => !member.isQualified).length,
    }));
}
