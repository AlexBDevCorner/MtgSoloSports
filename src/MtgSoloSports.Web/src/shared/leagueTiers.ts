/**
 * Shared tier/boundary presentation models for the multi-division pyramid
 * (MSS-060). Pure and DOM-free so every feature slice (standings, dashboard,
 * qualifiers, movement, rebalance, history, athletes) formats tier identity
 * identically. Tiers always come from backend data (`leagueLevel`,
 * `feederDivision`, `boundary`); league names are never parsed.
 */

/** Tier identity as the backend reports it (`LeagueLevel.ToString()`). */
export type LeagueLevel = 'Superleague' | 'Feeder1' | 'Feeder2' | 'Feeder3';

export function isLeagueLevel(value: string | null | undefined): value is LeagueLevel {
  return (
    value === 'Superleague' || value === 'Feeder1' || value === 'Feeder2' || value === 'Feeder3'
  );
}

/**
 * Display label for a tier. Historical v1 feeder rows predate the division
 * column (`feederDivision` 0/unknown with kind `Feeder`): they keep their
 * original single-feeder "Feeder" label instead of pretending F2/F3 existed.
 */
export function leagueLevelLabel(
  leagueLevel: string | null | undefined,
  feederDivision?: number | null,
  leagueKind?: string | null,
): string {
  const level = isLeagueLevel(leagueLevel) ? leagueLevel : null;
  const kind = leagueKind === 'Superleague' ? 'Superleague' : leagueKind === 'Feeder' ? 'Feeder' : null;
  if (level === 'Superleague' || (level === null && kind === 'Superleague')) {
    return 'Superleague';
  }
  if (level === 'Feeder2' || feederDivision === 2) {
    return 'Feeder 2';
  }
  if (level === 'Feeder3' || feederDivision === 3) {
    return 'Feeder 3';
  }
  // Feeder 1 with an explicit division, or any other feeder row carrying
  // division 1, reads as the top feeder tier.
  if (level === 'Feeder1' && feederDivision !== 0 && feederDivision != null) {
    return 'Feeder 1';
  }
  if (feederDivision === 1) {
    return 'Feeder 1';
  }
  // Historical v1 feeder rows (division 0/unknown) keep their original
  // single-feeder "Feeder" label.
  return 'Feeder';
}

/** Pyramid order: 0 is the top (Superleague), 3 is the bottom (Feeder 3). */
export function leagueLevelOrder(leagueLevel: string | null | undefined): number {
  switch (leagueLevel) {
    case 'Superleague':
      return 0;
    case 'Feeder1':
      return 1;
    case 'Feeder2':
      return 2;
    case 'Feeder3':
      return 3;
    default:
      return 1;
  }
}

/** Canonical tier bonus scale for display only (sporting math is backend-owned). */
export function tierBonusLabel(leagueLevel: string | null | undefined): string {
  switch (leagueLevel) {
    case 'Superleague':
      return '2×';
    case 'Feeder2':
      return '½×';
    case 'Feeder3':
      return '¼×';
    default:
      return '1×';
  }
}

/** One-line league-level explainer: bonus scale without cluttering score cells. */
export const TIER_BONUS_SCALE_LINE = 'Tier bonus scale — Superleague 2× · Feeder 1 1× · Feeder 2 ½× · Feeder 3 ¼×.';

/** Qualifier boundaries as the backend reports them (`QualifierBoundary.ToString()`). */
export type QualifierBoundary = 'Superleague' | 'Feeder1Feeder2' | 'Feeder2Feeder3';

export function isQualifierBoundary(value: string | null | undefined): value is QualifierBoundary {
  return value === 'Superleague' || value === 'Feeder1Feeder2' || value === 'Feeder2Feeder3';
}

/**
 * Human label for a qualifier event: "Superleague qualifier" or
 * "Feeder 1 ↔ Feeder 2 · White". Text labels only, never color alone.
 */
export function qualifierBoundaryLabel(
  boundary: string | null | undefined,
  sportingColorName?: string | null,
): string {
  if (boundary === 'Feeder1Feeder2') {
    return sportingColorName && sportingColorName !== '-'
      ? `Feeder 1 ↔ Feeder 2 · ${sportingColorName}`
      : 'Feeder 1 ↔ Feeder 2';
  }
  if (boundary === 'Feeder2Feeder3') {
    return sportingColorName && sportingColorName !== '-'
      ? `Feeder 2 ↔ Feeder 3 · ${sportingColorName}`
      : 'Feeder 2 ↔ Feeder 3';
  }
  if (sportingColorName && sportingColorName !== '-') {
    return `Superleague qualifier · ${sportingColorName}`;
  }
  return 'Superleague qualifier';
}

/** URL-safe route segment for a qualifier boundary. */
export function qualifierBoundarySegment(boundary: string): string {
  if (boundary === 'Feeder1Feeder2') {
    return 'f1f2';
  }
  if (boundary === 'Feeder2Feeder3') {
    return 'f2f3';
  }
  return 'superleague';
}

/** Parses a qualifier route segment back to the backend boundary name. */
export function qualifierBoundaryFromSegment(segment: string | null | undefined): QualifierBoundary | null {
  const normalized = (segment ?? '').trim().toLowerCase();
  if (normalized === 'f1f2' || normalized === 'f1-f2' || normalized === 'feeder1feeder2') {
    return 'Feeder1Feeder2';
  }
  if (normalized === 'f2f3' || normalized === 'f2-f3' || normalized === 'feeder2feeder3') {
    return 'Feeder2Feeder3';
  }
  if (normalized === 'superleague' || normalized === 'sl') {
    return 'Superleague';
  }
  return null;
}

/** Movement provenance group for reveal sectioning. */
export type MovementProvenance =
  | 'automatic'
  | 'qualifier-field'
  | 'qualifier-outcome'
  | 'structural'
  | 'inaugural'
  | 'other';

/** Groups backend movement kinds into automatic / qualifier / structural buckets. */
export function movementProvenance(kind: string | null | undefined): MovementProvenance {
  switch (kind) {
    case 'AutomaticPromotion':
    case 'AutomaticRelegation':
    case 'FeederAutomaticPromotion':
    case 'FeederAutomaticRelegation':
    case 'Safe':
      return 'automatic';
    case 'QualifierIncumbent':
    case 'QualifierChallenger':
    case 'FeederQualifierIncumbent':
    case 'FeederQualifierChallenger':
      return 'qualifier-field';
    case 'RebalanceDraw':
    case 'RebalanceDisplacement':
    case 'RebalanceUp':
    case 'RebalanceDown':
    case 'TierUpgradeSeed':
      return 'structural';
    case 'InauguralPromotion':
      return 'inaugural';
    default:
      return 'other';
  }
}

/** Short human label for a movement kind, paired with tier context by callers. */
export function movementKindLabel(kind: string | null | undefined): string {
  switch (kind) {
    case 'AutomaticPromotion':
    case 'FeederAutomaticPromotion':
      return 'Automatically promoted';
    case 'AutomaticRelegation':
    case 'FeederAutomaticRelegation':
      return 'Automatically relegated';
    case 'QualifierIncumbent':
    case 'FeederQualifierIncumbent':
      return 'Qualifier incumbent';
    case 'QualifierChallenger':
    case 'FeederQualifierChallenger':
      return 'Qualifier challenger';
    case 'RebalanceDraw':
      return 'Drawn from pool (structural)';
    case 'RebalanceDisplacement':
      return 'Displaced to pool (structural)';
    case 'RebalanceUp':
      return 'Moved up (structural)';
    case 'RebalanceDown':
      return 'Moved down (structural)';
    case 'TierUpgradeSeed':
      return 'Seeded into new division (structural)';
    case 'InauguralPromotion':
      return 'Inaugural promotion';
    case 'Safe':
      return 'Stayed (safe)';
    default:
      return kind && kind.length > 0 ? kind : 'Moved';
  }
}

export interface TieredLeagueRef {
  leagueId: number;
  name: string;
  kind: string;
  feederDivision?: number | null;
  leagueLevel?: string | null;
}

export interface LeagueTierGroups<T extends TieredLeagueRef> {
  superleague: T[];
  feeder1: T[];
  feeder2: T[];
  feeder3: T[];
  /** Legacy v1 single-feeder rows (division unknown/0, no level). */
  legacyFeeder: T[];
}

function tierBucket(league: TieredLeagueRef): keyof LeagueTierGroups<TieredLeagueRef> {
  if (league.kind === 'Superleague' || league.leagueLevel === 'Superleague') {
    return 'superleague';
  }
  // Division is authoritative for feeders: 1..3 are tiered, anything else
  // (0/unknown, including v1 rows labeled Feeder1 for compatibility) is the
  // legacy single feeder tier.
  if (league.feederDivision === 1) {
    return 'feeder1';
  }
  if (league.feederDivision === 2) {
    return 'feeder2';
  }
  if (league.feederDivision === 3) {
    return 'feeder3';
  }
  return 'legacyFeeder';
}

/**
 * Groups leagues into Superleague / Feeder 1 / Feeder 2 / Feeder 3 /
 * legacy-Feeder buckets from data (level first, then kind + division).
 * Each bucket sorts by name so colors read consistently. Never parses names.
 */
export function groupLeaguesByTier<T extends TieredLeagueRef>(leagues: readonly T[]): LeagueTierGroups<T> {
  const groups: LeagueTierGroups<T> = {
    superleague: [],
    feeder1: [],
    feeder2: [],
    feeder3: [],
    legacyFeeder: [],
  };
  for (const league of leagues) {
    groups[tierBucket(league)].push(league);
  }
  const byName = (a: T, b: T): number => a.name.localeCompare(b.name) || a.leagueId - b.leagueId;
  groups.superleague.sort(byName);
  groups.feeder1.sort(byName);
  groups.feeder2.sort(byName);
  groups.feeder3.sort(byName);
  groups.legacyFeeder.sort(byName);
  return groups;
}

/** "3 divisions · 24 leagues" style summary for tiered saves. */
export function pyramidSummary(leagues: readonly TieredLeagueRef[]): string {
  const groups = groupLeaguesByTier(leagues);
  const feederCount = groups.feeder1.length + groups.feeder2.length + groups.feeder3.length + groups.legacyFeeder.length;
  if (groups.feeder2.length > 0 || groups.feeder3.length > 0) {
    return `Superleague + Feeder 1–3 pyramid · ${groups.superleague.length + feederCount} leagues`;
  }
  return `${groups.superleague.length + feederCount} leagues`;
}
