/**
 * Presentation-only zone helpers for league standings.
 * Zones are visual only and never apply quotas or change sporting math.
 * Superleague (32 athletes): 1-16 safe, 17-24 qualifier, 25-32 relegation.
 * Legacy single feeder (32 athletes): 1 champion (auto-promoted), 2-4
 * qualifier, 5-32 safe.
 * Tiered pyramid (MSS-060), all 32 athletes:
 * - Feeder 1: 1 champion (promoted to Superleague), 2-4 Superleague
 *   qualifier challengers, 5-16 safe, 17-24 F1↔F2 qualifier incumbents,
 *   25-32 relegated to Feeder 2.
 * - Feeder 2: 1-8 promoted to Feeder 1, 9-16 F1↔F2 qualifier challengers,
 *   17-24 F2↔F3 qualifier incumbents, 25-32 relegated to Feeder 3.
 * - Feeder 3: 1-8 promoted to Feeder 2, 9-16 F2↔F3 qualifier challengers,
 *   17-32 safe.
 */

export type SuperleagueZone = 'safe' | 'qualifier' | 'relegation';
export type FeederZone = 'champion' | 'qualifier' | 'safe';
export type TieredFeederZone =
  | 'champion'
  | 'promoted'
  | 'qualifier-up'
  | 'qualifier-hold'
  | 'safe'
  | 'relegated';

export function superleagueZone(rank: number): SuperleagueZone {
  if (rank <= 16) {
    return 'safe';
  }
  if (rank <= 24) {
    return 'qualifier';
  }
  return 'relegation';
}

export function feederZone(rank: number): FeederZone {
  if (rank === 1) {
    return 'champion';
  }
  if (rank <= 4) {
    return 'qualifier';
  }
  return 'safe';
}

/** Tiered Feeder 1 zone for a final rank (see module doc for bands). */
export function feeder1Zone(rank: number): TieredFeederZone {
  if (rank === 1) {
    return 'champion';
  }
  if (rank <= 4) {
    return 'qualifier-up';
  }
  if (rank <= 16) {
    return 'safe';
  }
  if (rank <= 24) {
    return 'qualifier-hold';
  }
  return 'relegated';
}

/** Tiered Feeder 2 zone for a final rank (see module doc for bands). */
export function feeder2Zone(rank: number): TieredFeederZone {
  if (rank <= 8) {
    return 'promoted';
  }
  if (rank <= 16) {
    return 'qualifier-up';
  }
  if (rank <= 24) {
    return 'qualifier-hold';
  }
  return 'relegated';
}

/** Tiered Feeder 3 zone for a final rank (see module doc for bands). */
export function feeder3Zone(rank: number): TieredFeederZone {
  if (rank <= 8) {
    return 'promoted';
  }
  if (rank <= 16) {
    return 'qualifier-up';
  }
  return 'safe';
}

/**
 * Selects the zone mapping by league kind and tier. `leagueLevel` is the
 * backend tier identity; without it (or for legacy v1 rows) feeders use the
 * original single-feeder mapping.
 */
export function zoneForRank(
  rank: number,
  leagueKind: string,
  leagueLevel?: string | null,
): SuperleagueZone | FeederZone | TieredFeederZone {
  if (leagueKind === 'Superleague' || leagueLevel === 'Superleague') {
    return superleagueZone(rank);
  }
  if (leagueLevel === 'Feeder1') {
    return feeder1Zone(rank);
  }
  if (leagueLevel === 'Feeder2') {
    return feeder2Zone(rank);
  }
  if (leagueLevel === 'Feeder3') {
    return feeder3Zone(rank);
  }
  return feederZone(rank);
}

export function superleagueZoneLabel(zone: SuperleagueZone): string {
  switch (zone) {
    case 'safe':
      return 'Safe (1–16)';
    case 'qualifier':
      return 'Qualifier (17–24)';
    case 'relegation':
      return 'Relegation (25–32)';
  }
}

export function feederZoneLabel(zone: FeederZone): string {
  switch (zone) {
    case 'champion':
      return 'Champion · auto-promoted';
    case 'qualifier':
      return 'Qualifier (2–4)';
    case 'safe':
      return 'Safe';
  }
}

export function zoneLabelForRank(
  rank: number,
  leagueKind: string,
  leagueLevel?: string | null,
): string {
  const zone = zoneForRank(rank, leagueKind, leagueLevel);
  if (leagueKind === 'Superleague' || leagueLevel === 'Superleague') {
    return superleagueZoneLabel(zone as SuperleagueZone);
  }
  if (
    leagueLevel === 'Feeder1' ||
    leagueLevel === 'Feeder2' ||
    leagueLevel === 'Feeder3'
  ) {
    return tieredFeederZoneLabel(zone as TieredFeederZone);
  }
  return feederZoneLabel(zone as FeederZone);
}

export function tieredFeederZoneLabel(zone: TieredFeederZone): string {
  switch (zone) {
    case 'champion':
      return 'Champion · auto-promoted';
    case 'promoted':
      return 'Auto-promoted';
    case 'qualifier-up':
      return 'Qualifier · challenger';
    case 'qualifier-hold':
      return 'Qualifier · incumbent';
    case 'relegated':
      return 'Relegated';
    case 'safe':
      return 'Safe';
  }
}

/** One-line zone summary for footnotes, selected by tier. */
export function zoneSummaryLine(leagueKind: string, leagueLevel?: string | null): string {
  if (leagueKind === 'Superleague' || leagueLevel === 'Superleague') {
    return 'Superleague 1–16 safe, 17–24 qualifier, 25–32 relegated';
  }
  if (leagueLevel === 'Feeder1') {
    return 'Feeder 1: 1 champion promoted, 2–4 qualifier challengers, 5–16 safe, 17–24 qualifier incumbents, 25–32 relegated';
  }
  if (leagueLevel === 'Feeder2') {
    return 'Feeder 2: 1–8 promoted, 9–16 qualifier challengers, 17–24 qualifier incumbents, 25–32 relegated';
  }
  if (leagueLevel === 'Feeder3') {
    return 'Feeder 3: 1–8 promoted, 9–16 qualifier challengers, 17–32 safe';
  }
  return 'feeders champion auto-promoted plus 2–4 qualifier';
}

/** Display-only color composition: counts per sporting-color name. */
export function colorComposition(
  rows: ReadonlyArray<{ sportingColorName?: string | null; sportingColor?: number }>,
): Array<{ color: string; count: number }> {
  const counts = new Map<string, number>();
  for (const row of rows) {
    const key = (row.sportingColorName ?? `Color ${row.sportingColor ?? '?'}`).trim() || 'Unknown';
    counts.set(key, (counts.get(key) ?? 0) + 1);
  }
  return [...counts.entries()]
    .map(([color, count]) => ({ color, count }))
    .sort((a, b) => b.count - a.count || a.color.localeCompare(b.color));
}

/**
 * MSS-141 season-aware postseason zones.
 * Single source of rank-band definitions for the standings season table,
 * reusable by MSS-142 (which adds clinch math, not new bands).
 * Selection uses authoritative tier identity (kind + feederDivision first,
 * level as fallback) plus the persisted season number/phase. League names and
 * athlete colors are never parsed. Bands match the versioned sporting rules
 * (game-rules §§9–12): post-inaugural Superleague/F1/F2/F3, inaugural Season 1
 * (top 4 of each F1 enter the inaugural Superleague, no ordinary qualifiers
 * or automatic demotions), and legacy single-tier feeders (1 champion, 2–4
 * qualifier, rest retained; no new-format relegation bands).
 */

export type ZoneMovement =
  | 'promoted'
  | 'qualifier-up'
  | 'safe'
  | 'qualifier-hold'
  | 'relegated'
  | 'inaugural'
  | 'neutral';

export interface ZoneContext {
  leagueKind: string;
  leagueLevel?: string | null;
  /** Persisted feeder division (0/unknown legacy, 1..3 tiered). Authoritative for feeders. */
  feederDivision?: number | null;
  seasonNumber: number;
  /**
   * Whether the season's competition list contains tiered F2/F3 leagues.
   * Disambiguates persisted division 1: true proves tiered F1, false/unknown
   * means legacy single-feeder (v1 rows persist division 1 after migration, so
   * division alone cannot prove tiering). Derived from the season's actual
   * shape, never from names or colors.
   */
  hasTieredPyramid?: boolean | null;
}

export interface PostseasonZone {
  /** Stable id for styling, legend keys and MSS-142 reuse. */
  id: string;
  movement: ZoneMovement;
  /** Non-color glyph so meaning survives without color (paired with text). */
  symbol: string;
  /** Compact cell label. */
  shortLabel: string;
  /** Full accessible label with direction/target. */
  label: string;
  /** Rank band text, e.g. "1–16". */
  band: string;
  /** CSS class suffix, e.g. "zone-promoted". */
  cssClass: string;
}

function isSuperleagueTier(ctx: ZoneContext): boolean {
  return ctx.leagueKind === 'Superleague' || ctx.leagueLevel === 'Superleague';
}

function feederTier(ctx: ZoneContext): 'f1' | 'f2' | 'f3' | 'legacy' {
  // Divisions 2/3 only exist in tiered saves, so they prove tiering alone.
  // Division 1 is ambiguous: persisted v1 single-feeder rows carry First (1)
  // after migration (20261005120000_AddFeederDivision) and via SaveStore for
  // non-tiered rules, so only an explicit tiered season shape (F2/F3 present
  // in that season) promotes division 1 to tiered F1. Anything else stays
  // legacy to avoid asserting nonexistent qualifiers/relegations.
  if (ctx.feederDivision === 2) {
    return 'f2';
  }
  if (ctx.feederDivision === 3) {
    return 'f3';
  }
  if (ctx.feederDivision === 1 && ctx.hasTieredPyramid === true) {
    return 'f1';
  }
  return 'legacy';
}

function makeZone(
  id: string,
  movement: ZoneMovement,
  symbol: string,
  shortLabel: string,
  label: string,
  band: string,
  cssClass: string,
): PostseasonZone {
  return { id, movement, symbol, shortLabel, label, band, cssClass };
}

export const NEUTRAL_ZONE: PostseasonZone = makeZone(
  'neutral',
  'neutral',
  '—',
  'No zone',
  'No postseason zone in this format',
  '—',
  'zone-neutral',
);

/**
 * Resolves the postseason zone for an official season rank in a season/league.
 * Rank drives the zone (callers must pass the official season rank, never the
 * visual row index). Returns a neutral zone instead of a false movement when
 * the persisted source cannot reliably express a zone (unknown tier, or a
 * Superleague row in Season 1 which has no Superleague).
 */
export function resolvePostseasonZone(rank: number, ctx: ZoneContext): PostseasonZone {
  if (!Number.isInteger(rank) || rank < 1 || rank > 32) {
    return NEUTRAL_ZONE;
  }
  const season = Number.isInteger(ctx.seasonNumber) && ctx.seasonNumber >= 1 ? ctx.seasonNumber : 0;
  if (season === 0) {
    return NEUTRAL_ZONE;
  }
  const inaugural = season === 1;
  if (isSuperleagueTier(ctx)) {
    if (inaugural) {
      return NEUTRAL_ZONE;
    }
    if (rank <= 16) {
      return makeZone('sl-safe', 'safe', '●', 'Safe', 'Safe · retained in Superleague', '1–16', 'zone-safe');
    }
    if (rank <= 24) {
      return makeZone(
        'sl-qualifier',
        'qualifier-hold',
        '◐',
        'Qualifier · defending',
        'Superleague qualifier · defending (places 17–24)',
        '17–24',
        'zone-qualifier-hold',
      );
    }
    return makeZone(
      'sl-relegated',
      'relegated',
      '▼',
      'Relegated to F1',
      'Relegated to Feeder 1 (places 25–32)',
      '25–32',
      'zone-relegated',
    );
  }

  // Unknown league kinds cannot reliably express a zone; stay neutral instead
  // of asserting a false feeder movement.
  if (ctx.leagueKind !== 'Feeder') {
    return NEUTRAL_ZONE;
  }

  const tier = feederTier(ctx);
  if (inaugural) {
    // No ordinary qualifiers or automatic demotions exist in Season 1. Only
    // F1 ranks 1–4 enter the inaugural Superleague; every other inaugural
    // athlete is retained with no postseason qualifier/relegation band.
    if (tier === 'f1' || tier === 'legacy') {
      if (rank <= 4) {
        return makeZone(
          'inaugural-sl',
          'inaugural',
          '★',
          'Inaugural Superleague',
          'Inaugural Superleague entrant (places 1–4 of Feeder 1)',
          '1–4',
          'zone-inaugural',
        );
      }
      return makeZone(
        'inaugural-retained',
        'safe',
        '●',
        'Retained',
        'Retained in feeder · no qualifier or relegation in Season 1',
        tier === 'f1' ? '5–32' : '5–32',
        'zone-safe',
      );
    }
    return makeZone(
      'inaugural-retained',
      'safe',
      '●',
      'Retained',
      'Retained in feeder · no qualifier or relegation in Season 1',
      '1–32',
      'zone-safe',
    );
  }

  switch (tier) {
    case 'f1':
      if (rank === 1) {
        return makeZone(
          'f1-champion',
          'promoted',
          '★',
          'Champion · promoted',
          'Champion · automatically promoted to Superleague',
          '1',
          'zone-promoted',
        );
      }
      if (rank <= 4) {
        return makeZone(
          'f1-qualifier-up',
          'qualifier-up',
          '▲',
          'Qualifier · challenging',
          'Superleague qualifier · challenging (places 2–4)',
          '2–4',
          'zone-qualifier-up',
        );
      }
      if (rank <= 16) {
        return makeZone('f1-safe', 'safe', '●', 'Retained in F1', 'Retained in Feeder 1 (places 5–16)', '5–16', 'zone-safe');
      }
      if (rank <= 24) {
        return makeZone(
          'f1-qualifier-hold',
          'qualifier-hold',
          '◐',
          'F1↔F2 qualifier · defending',
          'Feeder 1 ↔ Feeder 2 qualifier · defending (places 17–24)',
          '17–24',
          'zone-qualifier-hold',
        );
      }
      return makeZone(
        'f1-relegated',
        'relegated',
        '▼',
        'Relegated to F2',
        'Relegated to Feeder 2 (places 25–32)',
        '25–32',
        'zone-relegated',
      );
    case 'f2':
      if (rank <= 8) {
        return makeZone(
          'f2-promoted',
          'promoted',
          '▲',
          'Promoted to F1',
          'Automatically promoted to Feeder 1 (places 1–8)',
          '1–8',
          'zone-promoted',
        );
      }
      if (rank <= 16) {
        return makeZone(
          'f2-qualifier-up',
          'qualifier-up',
          '▲',
          'F1↔F2 qualifier · challenging',
          'Feeder 1 ↔ Feeder 2 qualifier · challenging (places 9–16)',
          '9–16',
          'zone-qualifier-up',
        );
      }
      if (rank <= 24) {
        return makeZone(
          'f2-qualifier-hold',
          'qualifier-hold',
          '◐',
          'F2↔F3 qualifier · defending',
          'Feeder 2 ↔ Feeder 3 qualifier · defending (places 17–24)',
          '17–24',
          'zone-qualifier-hold',
        );
      }
      return makeZone(
        'f2-relegated',
        'relegated',
        '▼',
        'Relegated to F3',
        'Relegated to Feeder 3 (places 25–32)',
        '25–32',
        'zone-relegated',
      );
    case 'f3':
      if (rank <= 8) {
        return makeZone(
          'f3-promoted',
          'promoted',
          '▲',
          'Promoted to F2',
          'Automatically promoted to Feeder 2 (places 1–8)',
          '1–8',
          'zone-promoted',
        );
      }
      if (rank <= 16) {
        return makeZone(
          'f3-qualifier-up',
          'qualifier-up',
          '▲',
          'F2↔F3 qualifier · challenging',
          'Feeder 2 ↔ Feeder 3 qualifier · challenging (places 9–16)',
          '9–16',
          'zone-qualifier-up',
        );
      }
      return makeZone(
        'f3-safe',
        'safe',
        '●',
        'Retained in F3',
        'Retained in Feeder 3 (places 17–32) · no ordinary relegation to Pool',
        '17–32',
        'zone-safe',
      );
    case 'legacy':
    default:
      // Historical single-tier/v1 path: champion + 2–4 qualifier only. Never
      // assert tiered relegation/qualifier-hold bands that did not exist.
      if (rank === 1) {
        return makeZone(
          'legacy-champion',
          'promoted',
          '★',
          'Champion · promoted',
          'Champion · automatically promoted to Superleague',
          '1',
          'zone-promoted',
        );
      }
      if (rank <= 4) {
        return makeZone(
          'legacy-qualifier',
          'qualifier-up',
          '▲',
          'Qualifier · challenging',
          'Superleague qualifier · challenging (places 2–4)',
          '2–4',
          'zone-qualifier-up',
        );
      }
      return makeZone(
        'legacy-safe',
        'safe',
        '●',
        'Retained',
        'Retained in feeder (places 5–32)',
        '5–32',
        'zone-safe',
      );
  }
}

/** Distinct zones in rank order for the legend/key of one season/league. */
export function zoneLegendFor(ctx: ZoneContext): PostseasonZone[] {
  const seen = new Map<string, PostseasonZone>();
  for (let rank = 1; rank <= 32; rank += 1) {
    const zone = resolvePostseasonZone(rank, ctx);
    if (!seen.has(zone.id)) {
      seen.set(zone.id, zone);
    }
  }
  return [...seen.values()];
}

/**
 * Provisional vs final wording. Before the season is officially complete no
 * row claims its placement is guaranteed.
 */
export function zoneStatusPrefix(isSeasonComplete: boolean): string {
  return isSeasonComplete ? 'Final:' : 'Currently in';
}

export function zoneProvisionalNote(isSeasonComplete: boolean): string {
  return isSeasonComplete
    ? 'Final positions after the completed season. Qualifier results and later rebalancing are separate events.'
    : 'Provisional zones — if the season ended now. Nothing is guaranteed until all 32 stages complete; qualifier results and later rebalancing are separate events.';
}
