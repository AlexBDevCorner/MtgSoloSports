import type {
  TypeCupDraw,
  TypeCupTournament,
  TypeCupTournamentLeg,
  TypeCupTournamentQualificationGroup,
} from './typeCupTournamentApi';

/**
 * Pure display helpers for the scalable Type Cup tournament (MSS-063, MSS-071).
 * DOM-free: group letters, stage labels and cutoff splits derive from
 * persisted backend facts only. The frontend never computes who qualified;
 * `qualified` flags, quotas, guaranteed places and wildcards come from the
 * backend responses.
 */

/** 1-based letter: 1 -> A, 2 -> B, ... 27 -> AA. Persisted numbers stay authoritative. */
export function qualificationGroupLetter(qualificationGroup: number): string {
  if (!Number.isInteger(qualificationGroup) || qualificationGroup < 1) {
    return String(qualificationGroup);
  }
  let value = qualificationGroup;
  let letter = '';
  while (value > 0) {
    value -= 1;
    letter = String.fromCharCode(65 + (value % 26)) + letter;
    value = Math.floor(value / 26);
  }
  return letter;
}

/** Generic stage label that scales beyond two groups. */
export function qualificationGroupName(qualificationGroup: number): string {
  return `Qualification Group ${qualificationGroupLetter(qualificationGroup)}`;
}

/**
 * Friendly secondary label for two-group fields ("Semifinal A/B").
 * Returns null for any other group count so the UI never pretends every
 * stage is literally a semifinal.
 */
export function semifinalAlias(qualificationGroup: number, groupCount: number): string | null {
  if (groupCount !== 2) {
    return null;
  }
  if (qualificationGroup === 1) {
    return 'Semifinal A';
  }
  if (qualificationGroup === 2) {
    return 'Semifinal B';
  }
  return null;
}

/** Full stage label with the generic identity first and the friendly alias second. */
export function qualificationStageLabel(qualificationGroup: number, groupCount: number): string {
  const generic = qualificationGroupName(qualificationGroup);
  const alias = semifinalAlias(qualificationGroup, groupCount);
  return alias ? `${generic} · ${alias}` : generic;
}

/** Athlete rank-group label inside a stage (never confused with the qualification group). */
export function rankGroupLabel(groupNumber: number): string {
  return `Squad #${groupNumber}`;
}

/**
 * Data-driven leg context: which sporting stage the leg belongs to plus the
 * athlete rank group inside that stage.
 */
export function legStageLabel(args: {
  isDirectFinal: boolean;
  isFinalLeg: boolean;
  qualificationGroup: number | null;
  groupCount: number;
}): string {
  if (args.isDirectFinal || args.isFinalLeg) {
    return 'Type Cup Final';
  }
  if (args.qualificationGroup !== null) {
    return qualificationStageLabel(args.qualificationGroup, args.groupCount);
  }
  return 'Type Cup';
}

/** Tournament format wording for the overview. */
export function tournamentFormatLabel(
  tournament: Pick<TypeCupTournament, 'isDirectFinal' | 'qualificationPolicyVersion'>,
): string {
  if (tournament.isDirectFinal) {
    return 'Direct Final';
  }
  return tournament.qualificationPolicyVersion === 2
    ? 'Qualification + Final · guaranteed + wildcards'
    : 'Qualification + Final';
}

/** Honest advancing label: guaranteed per group plus global wildcards (MSS-071). */
export function advancingLabel(tournament: Pick<TypeCupTournament, 'qualificationPolicyVersion' | 'wildcardCount' | 'guaranteedPlacesPerGroup' | 'finalPlacesPerGroup'>): string {
  const wildcards = tournament.wildcardCount ?? 0;
  if ((tournament.qualificationPolicyVersion ?? 1) === 2 && wildcards > 0) {
    const guaranteed = tournament.guaranteedPlacesPerGroup ?? tournament.finalPlacesPerGroup;
    return `${guaranteed.join(' / ')} guaranteed + ${wildcards} wildcard${wildcards === 1 ? '' : 's'}`;
  }
  return (tournament.finalPlacesPerGroup ?? []).join(' / ');
}

/** Whether the tournament uses the wildcard policy. */
export function isWildcardTournament(
  tournament: Pick<TypeCupTournament, 'qualificationPolicyVersion' | 'wildcardCount'>,
): boolean {
  return (tournament.qualificationPolicyVersion ?? 1) === 2 && (tournament.wildcardCount ?? 0) > 0;
}

/** Status badge text for a qualification team. */
export function qualificationStatusLabel(status: string | undefined, qualified: boolean): string {
  if (status === 'Guaranteed') {
    return 'Qualified · guaranteed';
  }
  if (status === 'Wildcard') {
    return 'Qualified · wildcard';
  }
  if (status === 'Qualified') {
    return 'Qualified';
  }
  return qualified ? 'Qualified' : 'Eliminated';
}

/** Current tournament state from persisted facts (no resimulation). */
export function tournamentStateLabel(tournament: TypeCupTournament): string {
  if (tournament.isDirectFinal) {
    return tournament.final ? 'Completed' : 'In progress';
  }
  const groupsDone = tournament.qualificationGroups.length;
  if (groupsDone < tournament.qualificationGroupCount) {
    return `In progress — ${groupsDone} of ${tournament.qualificationGroupCount} qualification groups complete`;
  }
  return tournament.final ? 'Completed' : 'Qualification complete — Final pending';
}

/** Teams above the cutoff (qualified) and below it (eliminated) in rank order. */
export function splitQualificationTable<T extends { teamRank: number; qualified: boolean }>(
  teams: readonly T[],
  finalPlaces: number,
): { qualified: T[]; eliminated: T[]; cutoffAfter: number } {
  const ordered = [...teams].sort((a, b) => a.teamRank - b.teamRank);
  return {
    qualified: ordered.filter((team) => team.qualified),
    eliminated: ordered.filter((team) => !team.qualified),
    cutoffAfter: finalPlaces,
  };
}

/** Qualification group in rank order (display only; backend ranked it). */
export function orderedQualificationTeams(group: TypeCupTournamentQualificationGroup) {
  return [...group.teams].sort((a, b) => a.teamRank - b.teamRank);
}

/** Legs of one rank group in finishing order. */
export function legsByRankGroup(
  legs: readonly TypeCupTournamentLeg[],
): { groupNumber: number; legs: TypeCupTournamentLeg[] }[] {
  const numbers = [...new Set(legs.map((leg) => leg.groupNumber))].sort((a, b) => a - b);
  return numbers.map((groupNumber) => ({
    groupNumber,
    legs: legs
      .filter((leg) => leg.groupNumber === groupNumber)
      .sort((a, b) => a.groupRank - b.groupRank),
  }));
}

/** Draw members in stable order for display (backend order is authoritative for the draw itself). */
export function orderedDrawMembers(creatureTypes: readonly string[]): string[] {
  return [...creatureTypes].sort((a, b) => a.localeCompare(b));
}

/** Whether the tournament response carries any qualification stage. */
export function hasQualificationStage(tournament: Pick<TypeCupTournament, 'isDirectFinal'>): boolean {
  return !tournament.isDirectFinal;
}

/** Whether a draw response carries qualification groups. */
export function drawHasGroups(draw: Pick<TypeCupDraw, 'isDirectFinal' | 'groups'>): boolean {
  return !draw.isDirectFinal && draw.groups.length > 0;
}
