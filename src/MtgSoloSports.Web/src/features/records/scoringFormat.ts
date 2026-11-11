import type { ScoringRecord, ScoringRecordHolder } from './recordsApi';

/** Display-only context for one scoring holder (no sporting math). */
export function formatScoringContext(holder: ScoringRecordHolder): string {
  const parts: string[] = [`Season ${holder.seasonNumber}`, holder.competition];
  if (holder.leagueName && holder.leagueName !== holder.competition) {
    parts.push(holder.leagueName);
  }
  if (holder.groupNumber !== null && holder.groupNumber !== undefined) {
    parts.push(`Group ${holder.groupNumber}`);
  }
  if (holder.stageNumber !== null && holder.stageNumber !== undefined) {
    parts.push(`Stage ${holder.stageNumber}`);
  }
  if (holder.roundNumber !== null && holder.roundNumber !== undefined) {
    parts.push(`Round ${holder.roundNumber}`);
  }
  return parts.join(' · ');
}

export function groupScoringRecords(records: ScoringRecord[]): {
  league: ScoringRecord[];
  individual: ScoringRecord[];
  team: ScoringRecord[];
} {
  return {
    league: records.filter((r) => r.category === 'League'),
    individual: records.filter((r) => r.category === 'Individual Cups'),
    team: records.filter((r) => r.category === 'Team Cups'),
  };
}

/**
 * MSS-140 structured League scoring grid identity. Scope order matches the
 * backend `ScoreRecordKey.LeagueScopes` (8 feeder colors, Superleague
 * last); metrics are the stable `Metric` values (`round` | `stage` |
 * `season`). Placement uses `scopeKey`/`metric` only — never display
 * labels — with a stable-record-key fallback for payloads that predate
 * the additive fields.
 */
export const LEAGUE_SCOPE_ORDER: readonly string[] = Object.freeze([
  'white',
  'blue',
  'black',
  'red',
  'green',
  'multicolor',
  'hybrid',
  'colorless',
  'superleague',
]);

export const LEAGUE_METRIC_ORDER: readonly string[] = Object.freeze(['round', 'stage', 'season']);

/** Stable Cup event order for grouped Individual/Team Cup sections. */
export const CUP_EVENT_ORDER: readonly string[] = Object.freeze([
  'colour_cup_individual',
  'qualifier',
  'colour_cup_team',
  'type_cup_team',
]);

const CUP_METRIC_ORDER: readonly string[] = Object.freeze([
  'round',
  'stage',
  'season',
  'total',
  'team_round',
]);

/** MSS-140: about 10 holders render inline before a "+N more" toggle. */
export const CAREER_HOLDER_PREVIEW_COUNT = 10;

const LEAGUE_SCOPE_LABELS: Record<string, string> = {
  white: 'White League',
  blue: 'Blue League',
  black: 'Black League',
  red: 'Red League',
  green: 'Green League',
  multicolor: 'Multicolor League',
  hybrid: 'Hybrid League',
  colorless: 'Colorless League',
  superleague: 'Superleague',
};

const LEAGUE_METRIC_LABELS: Record<string, string> = {
  round: 'Single round',
  stage: 'Single stage',
  season: 'Season points',
};

const CUP_EVENT_LABELS: Record<string, string> = {
  colour_cup_individual: 'Color Cup individual',
  qualifier: 'Qualifier',
  colour_cup_team: 'Color Cup team',
  type_cup_team: 'Type Cup team',
};

/** Row header for one League grid scope (stable key in, display text out). */
export function leagueScopeDisplayName(scopeKey: string | null | undefined): string {
  if (typeof scopeKey === 'string' && Object.prototype.hasOwnProperty.call(LEAGUE_SCOPE_LABELS, scopeKey)) {
    return LEAGUE_SCOPE_LABELS[scopeKey]!;
  }
  return 'Unknown league';
}

/** Column header for one League grid metric (stable key in, display text out). */
export function leagueMetricDisplayName(metric: string | null | undefined): string {
  if (typeof metric === 'string' && Object.prototype.hasOwnProperty.call(LEAGUE_METRIC_LABELS, metric)) {
    return LEAGUE_METRIC_LABELS[metric]!;
  }
  return 'Unknown metric';
}

/** Section header for one Cup event group (stable key in, display text out). */
export function cupEventDisplayName(eventKey: string | null | undefined): string {
  if (typeof eventKey === 'string' && Object.prototype.hasOwnProperty.call(CUP_EVENT_LABELS, eventKey)) {
    return CUP_EVENT_LABELS[eventKey]!;
  }
  return 'Unknown event';
}

/**
 * Stable scope identity for one scoring record. Prefers the additive
 * `scopeKey`; falls back to parsing the stable `recordKey` (never display
 * labels) so older payloads still place correctly.
 */
export function resolveScoringScopeKey(record: ScoringRecord): string | null {
  if (typeof record.scopeKey === 'string' && record.scopeKey.trim().length > 0) {
    return record.scopeKey.trim();
  }
  const key = record.recordKey;
  if (typeof key === 'string') {
    const league = /^league_(?:single_round|stage_best|points_best)__([a-z]+)$/.exec(key);
    if (league) {
      return league[1]!;
    }
    if (key === 'colour_cup_individual_round_best' || key === 'colour_cup_individual_stage_best') {
      return 'colour_cup_individual';
    }
    if (key === 'qualifier_single_round_best' || key === 'qualifier_stage_best') {
      return 'qualifier';
    }
    if (key.startsWith('colour_cup_team_')) {
      return 'colour_cup_team';
    }
    if (key.startsWith('type_cup_team_')) {
      return 'type_cup_team';
    }
  }
  return null;
}

/**
 * Stable metric identity for one scoring record. Prefers the additive
 * `metric`; falls back to the stable `recordKey` (never display labels).
 */
export function resolveScoringMetric(record: ScoringRecord): string | null {
  if (typeof record.metric === 'string' && record.metric.trim().length > 0) {
    return record.metric.trim();
  }
  const key = record.recordKey;
  if (typeof key !== 'string') {
    return null;
  }
  if (
    key === 'colour_cup_team_total_best' ||
    key === 'type_cup_team_total_best'
  ) {
    return 'total';
  }
  if (key === 'colour_cup_team_round_best' || key === 'type_cup_team_round_best') {
    return 'team_round';
  }
  if (key.includes('points_best')) {
    return 'season';
  }
  if (key.includes('stage_best') || key.includes('_stage_')) {
    return 'stage';
  }
  if (key.includes('single_round') || key.includes('_round_')) {
    return 'round';
  }
  return null;
}

export interface LeagueGridRow {
  scopeKey: string;
  round: ScoringRecord | undefined;
  stage: ScoringRecord | undefined;
  season: ScoringRecord | undefined;
}

/**
 * One League grid row per scope in `LEAGUE_SCOPE_ORDER`, each cell placed
 * by stable scope/metric identity. Missing cells stay undefined so the
 * table renders Vacant. League records with an unrecognized scope or
 * metric are ignored rather than misplaced.
 */
export function buildLeagueGrid(records: ScoringRecord[]): LeagueGridRow[] {
  const byScope = new Map<string, Map<string, ScoringRecord>>();
  for (const record of records) {
    if (record.category !== 'League') {
      continue;
    }
    const scopeKey = resolveScoringScopeKey(record);
    const metric = resolveScoringMetric(record);
    if (scopeKey === null || metric === null) {
      continue;
    }
    if (!LEAGUE_SCOPE_ORDER.includes(scopeKey) || !LEAGUE_METRIC_ORDER.includes(metric)) {
      continue;
    }
    let row = byScope.get(scopeKey);
    if (!row) {
      row = new Map<string, ScoringRecord>();
      byScope.set(scopeKey, row);
    }
    if (!row.has(metric)) {
      row.set(metric, record);
    }
  }
  return LEAGUE_SCOPE_ORDER.map((scopeKey) => {
    const row = byScope.get(scopeKey);
    return {
      scopeKey,
      round: row?.get('round'),
      stage: row?.get('stage'),
      season: row?.get('season'),
    };
  });
}

export interface CupEventGroup {
  eventKey: string;
  records: ScoringRecord[];
}

/**
 * Cup records grouped by stable event scope for one category
 * (`Individual Cups` or `Team Cups`). Groups follow `CUP_EVENT_ORDER`;
 * records inside a group follow metric order. Records from other
 * categories or with unrecognized events are ignored.
 */
export function groupCupRecords(
  records: ScoringRecord[],
  category: 'Individual Cups' | 'Team Cups',
): CupEventGroup[] {
  const wanted =
    category === 'Individual Cups'
      ? ['colour_cup_individual', 'qualifier']
      : ['colour_cup_team', 'type_cup_team'];
  const groups = new Map<string, ScoringRecord[]>();
  for (const record of records) {
    if (record.category !== category) {
      continue;
    }
    const scopeKey = resolveScoringScopeKey(record);
    if (scopeKey === null || !wanted.includes(scopeKey)) {
      continue;
    }
    let list = groups.get(scopeKey);
    if (!list) {
      list = [];
      groups.set(scopeKey, list);
    }
    list.push(record);
  }
  const metricRank = (record: ScoringRecord): number => {
    const index = CUP_METRIC_ORDER.indexOf(resolveScoringMetric(record) ?? '');
    return index === -1 ? CUP_METRIC_ORDER.length : index;
  };
  return wanted
    .filter((eventKey) => groups.has(eventKey))
    .map((eventKey) => ({
      eventKey,
      records: (groups.get(eventKey) ?? [])
        .slice()
        .sort((a, b) => {
          const rank = metricRank(a) - metricRank(b);
          return rank !== 0 ? rank : a.recordKey.localeCompare(b.recordKey);
        }),
    }));
}

/**
 * Split a holder list into the inline preview and the hidden remainder.
 * Shows the first `preview` holders; the caller renders a "+N more" toggle
 * when `hiddenCount` is positive.
 */
export function splitHolderPreview<T>(
  holders: readonly T[],
  preview: number = CAREER_HOLDER_PREVIEW_COUNT,
): { visible: T[]; hiddenCount: number } {
  if (!Number.isInteger(preview) || preview < 0) {
    return { visible: holders.slice(), hiddenCount: 0 };
  }
  if (holders.length <= preview) {
    return { visible: holders.slice(), hiddenCount: 0 };
  }
  return { visible: holders.slice(0, preview), hiddenCount: holders.length - preview };
}
