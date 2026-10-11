import type {
  AthleteCupHistory,
  AthleteCupSelection,
  AthleteSeason,
} from './athleteApi';

/**
 * At-a-glance career summary for the Athlete Profile pane (MSS-076).
 * Pure and DOM-free: derives display totals from the same persisted
 * profile payload the detailed panes render, without re-simulating,
 * parsing league names, or duplicating gameplay calculations.
 *
 * - Seasons count each season summary once (one row per season per
 *   athlete in the transactional projections). `wasActive === false`
 *   is a Pool season; otherwise the tier comes from data
 *   (`leagueLevel` / `feederDivision` / `leagueKind`), never names.
 * - Titles count `isChampion` seasons per tier, distinct from appearances.
 * - Caps count distinct Cup selections per kind (one cap per season),
 *   matching the Cup roster definition (`picks.Count()`), so a Color Cup
 *   individual + team double header or a Type Cup qualification + Final
 *   pair never counts twice.
 * - Medals count each completed Cup history row once by its persisted
 *   medal (`Gold` / `Silver` / `Bronze`). Qualification-only and
 *   non-podium rows persist `None` and are ignored. League titles are
 *   never included here.
 */

export type SeasonTier = 'superleague' | 'feeder1' | 'feeder2' | 'feeder3' | 'pool';

export interface SeasonTierCounts {
  superleague: number;
  feeder1: number;
  feeder2: number;
  feeder3: number;
  pool: number;
}

export interface TitleTierCounts {
  superleague: number;
  feeder1: number;
  feeder2: number;
  feeder3: number;
}

export interface CupCaps {
  color: number;
  type: number;
}

export interface CupMedals {
  gold: number;
  silver: number;
  bronze: number;
}

export interface AthleteCareerSummary {
  seasons: SeasonTierCounts;
  titles: TitleTierCounts;
  caps: CupCaps;
  medals: CupMedals;
}

export function emptySeasonTierCounts(): SeasonTierCounts {
  return { superleague: 0, feeder1: 0, feeder2: 0, feeder3: 0, pool: 0 };
}

export function emptyTitleTierCounts(): TitleTierCounts {
  return { superleague: 0, feeder1: 0, feeder2: 0, feeder3: 0 };
}

/**
 * Resolves one season summary to its career tier.
 * Returns `pool` for inactive seasons. Active seasons resolve from
 * `leagueLevel` first, then `feederDivision`, then `leagueKind`
 * (1 = Superleague). Historical v1 feeder rows (division 0/unknown)
 * count as Feeder 1, matching the backend canonical mapping and the
 * prestige rule that v1 feeder titles count as Feeder 1 — without
 * pretending F2/F3 seasons occurred.
 */
export function tierForSeason(season: Pick<AthleteSeason, 'wasActive' | 'leagueLevel' | 'feederDivision' | 'leagueKind'>): SeasonTier {
  if (!season.wasActive) {
    return 'pool';
  }
  const level = season.leagueLevel ?? null;
  const division = season.feederDivision ?? null;
  if (level === 'Superleague') {
    return 'superleague';
  }
  if (level === 'Feeder2' || division === 2) {
    return 'feeder2';
  }
  if (level === 'Feeder3' || division === 3) {
    return 'feeder3';
  }
  // Superleague without a level string (legacy payloads) still resolves
  // from the numeric league kind.
  if ((level === null || level === undefined) && season.leagueKind === 1) {
    return 'superleague';
  }
  // Everything else active is Feeder 1: explicit Feeder1, division 1, or
  // historical v1 single-feeder rows (division 0/unknown).
  return 'feeder1';
}

/** Counts seasons per tier; each season summary counts exactly once. */
export function summarizeSeasonTiers(
  seasons: readonly Pick<AthleteSeason, 'wasActive' | 'leagueLevel' | 'feederDivision' | 'leagueKind'>[],
): SeasonTierCounts {
  const counts = emptySeasonTierCounts();
  for (const season of seasons) {
    counts[tierForSeason(season)] += 1;
  }
  return counts;
}

/**
 * Counts championships per tier; each `isChampion` season counts once
 * in its own tier. Pool seasons never carry `isChampion`.
 */
export function summarizeTitleTiers(
  seasons: readonly Pick<
    AthleteSeason,
    'wasActive' | 'leagueLevel' | 'feederDivision' | 'leagueKind' | 'isChampion'
  >[],
): TitleTierCounts {
  const counts = emptyTitleTierCounts();
  for (const season of seasons) {
    if (!season.isChampion) {
      continue;
    }
    const tier = tierForSeason(season);
    if (tier === 'pool') {
      continue;
    }
    counts[tier] += 1;
  }
  return counts;
}

function isColorSelection(kind: string | null | undefined): boolean {
  return kind === 'ColorCup';
}

function isTypeSelection(kind: string | null | undefined): boolean {
  return kind === 'TypeCup';
}

/**
 * Counts distinct Cup selections per kind. One selection row is one cap;
 * deduplicates by (kind, season) so a duplicated row can never inflate
 * the total. Unknown kinds are ignored rather than guessed.
 */
export function summarizeCaps(
  selections: readonly Pick<AthleteCupSelection, 'cupKind' | 'sourceSeasonNumber'>[],
): CupCaps {
  const colorSeasons = new Set<number>();
  const typeSeasons = new Set<number>();
  for (const selection of selections) {
    if (isColorSelection(selection.cupKind)) {
      colorSeasons.add(selection.sourceSeasonNumber);
    } else if (isTypeSelection(selection.cupKind)) {
      typeSeasons.add(selection.sourceSeasonNumber);
    }
  }
  return { color: colorSeasons.size, type: typeSeasons.size };
}

/**
 * Counts persisted Cup medals across all Cup types. Each history row is
 * one event (Color individual, Color team, Type team stage) and counts
 * at most once. `None`/missing medals (qualification-only legs,
 * non-podium finishes) contribute nothing.
 */
export function summarizeMedals(
  history: readonly Pick<AthleteCupHistory, 'medal'>[],
): CupMedals {
  let gold = 0;
  let silver = 0;
  let bronze = 0;
  for (const entry of history) {
    if (entry.medal === 'Gold') {
      gold += 1;
    } else if (entry.medal === 'Silver') {
      silver += 1;
    } else if (entry.medal === 'Bronze') {
      bronze += 1;
    }
  }
  return { gold, silver, bronze };
}

export function summarizeCareer(
  seasons: readonly Pick<
    AthleteSeason,
    'wasActive' | 'leagueLevel' | 'feederDivision' | 'leagueKind' | 'isChampion'
  >[],
  selections: readonly Pick<AthleteCupSelection, 'cupKind' | 'sourceSeasonNumber'>[],
  history: readonly Pick<AthleteCupHistory, 'medal'>[],
): AthleteCareerSummary {
  return {
    seasons: summarizeSeasonTiers(seasons),
    titles: summarizeTitleTiers(seasons),
    caps: summarizeCaps(selections),
    medals: summarizeMedals(history),
  };
}
