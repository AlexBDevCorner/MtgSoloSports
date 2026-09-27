/**
 * Presentation-only zone helpers for league standings.
 * Zones are visual only and never apply quotas or change sporting math.
 * Superleague (32 athletes): 1-16 safe, 17-24 qualifier, 25-32 relegation.
 * Feeder (32 athletes): 1 champion (auto-promoted), 2-4 qualifier, 5-32 safe.
 */

export type SuperleagueZone = 'safe' | 'qualifier' | 'relegation';
export type FeederZone = 'champion' | 'qualifier' | 'safe';

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

export function zoneForRank(rank: number, leagueKind: string): SuperleagueZone | FeederZone {
  return leagueKind === 'Superleague' ? superleagueZone(rank) : feederZone(rank);
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

export function zoneLabelForRank(rank: number, leagueKind: string): string {
  const zone = zoneForRank(rank, leagueKind);
  return leagueKind === 'Superleague'
    ? superleagueZoneLabel(zone as SuperleagueZone)
    : feederZoneLabel(zone as FeederZone);
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
