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
