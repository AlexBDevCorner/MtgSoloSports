import type { ColorMedalRow, CupTeamSummary } from './cupHistoryApi';

export type ColorMedalView = 'team' | 'individual' | 'combined';

export const COLOR_MEDAL_VIEWS: readonly ColorMedalView[] = ['team', 'individual', 'combined'] as const;

export function medalViewLabel(view: ColorMedalView): string {
  switch (view) {
    case 'team':
      return 'Team';
    case 'individual':
      return 'Individual';
    case 'combined':
      return 'Combined';
  }
}

export function medalViewHeading(view: ColorMedalView): string {
  return `All-time medals by color — ${medalViewLabel(view)}`;
}

export function medalViewDescription(view: ColorMedalView): string {
  switch (view) {
    case 'team':
      return 'Team medals: one medal per Color Cup team podium for its color.';
    case 'individual':
      return 'Individual medals: one medal per Color Cup individual podium, credited to the color the athlete represented in that edition.';
    case 'combined':
      return 'Combined medals: team medals plus individual medals for each color.';
  }
}

/** Total medals for one color row (gold + silver + bronze, fixed-point safe integers). */
export function medalTotal(row: Pick<ColorMedalRow, 'gold' | 'silver' | 'bronze'>): number {
  return row.gold + row.silver + row.bronze;
}

/**
 * Client-side ordering matching the backend team table: gold, then silver,
 * then bronze, then team name. The backend already returns sorted rows; this
 * keeps fallback/recomputed tables consistent.
 */
export function sortMedalRows<T extends { gold: number; silver: number; bronze: number; teamName: string }>(
  rows: readonly T[],
): T[] {
  return [...rows].sort(
    (a, b) => b.gold - a.gold || b.silver - a.silver || b.bronze - a.bronze || (a.teamName < b.teamName ? -1 : a.teamName > b.teamName ? 1 : 0),
  );
}

export function toMedalRows(teams: readonly CupTeamSummary[]): ColorMedalRow[] {
  return teams.map((team) => ({
    teamKey: team.teamKey,
    teamName: team.teamName,
    gold: team.gold,
    silver: team.silver,
    bronze: team.bronze,
  }));
}

/**
 * Backward-compatible fallback when an older payload lacks the combined
 * table: exact per-color, per-medal sum of the team and individual tables.
 */
export function combineMedalRows(
  teams: readonly Pick<ColorMedalRow, 'teamKey' | 'teamName' | 'gold' | 'silver' | 'bronze'>[],
  individuals: readonly Pick<ColorMedalRow, 'teamKey' | 'teamName' | 'gold' | 'silver' | 'bronze'>[],
): ColorMedalRow[] {
  const byKey = new Map<string, ColorMedalRow>();
  for (const team of teams) {
    byKey.set(team.teamKey, {
      teamKey: team.teamKey,
      teamName: team.teamName,
      gold: team.gold,
      silver: team.silver,
      bronze: team.bronze,
    });
  }
  for (const single of individuals) {
    const existing = byKey.get(single.teamKey);
    if (existing) {
      existing.gold += single.gold;
      existing.silver += single.silver;
      existing.bronze += single.bronze;
    } else {
      byKey.set(single.teamKey, {
        teamKey: single.teamKey,
        teamName: single.teamName,
        gold: single.gold,
        silver: single.silver,
        bronze: single.bronze,
      });
    }
  }
  return sortMedalRows([...byKey.values()]);
}
