import {
  qualificationGroupLetter,
  qualificationStageLabel,
  rankGroupLabel,
} from '../cups/typeCupTournamentModel';
import type { PlayedRound } from '../events/eventsApi';

/** Stage identity for a Type Cup tournament stage (MSS-078/079). */
export interface TypeCupStageKey {
  tournamentPhase: number;
  qualificationGroup: number;
}

export type TypeCupStageKind = 'qualification' | 'final' | 'single';

export type TypeCupStageStatus = 'completed' | 'active' | 'pending';

export interface TypeCupStageInfo {
  key: TypeCupStageKey;
  kind: TypeCupStageKind;
  /** 1-based qualification number for quals, null for Final/single. */
  qualificationGroup: number | null;
  label: string;
  shortLabel: string;
  status: TypeCupStageStatus;
  played: number;
  total: number;
}

export function stageKeyEquals(a: TypeCupStageKey | null, b: TypeCupStageKey | null): boolean {
  if (a === null || b === null) {
    return a === b;
  }
  return a.tournamentPhase === b.tournamentPhase && a.qualificationGroup === b.qualificationGroup;
}

export function stageKeyString(key: TypeCupStageKey): string {
  return `${key.tournamentPhase}/${key.qualificationGroup}`;
}

export function isFinalStageKey(key: TypeCupStageKey): boolean {
  return key.tournamentPhase === 2;
}

export function isQualificationStageKey(key: TypeCupStageKey): boolean {
  return key.tournamentPhase === 1;
}

/** Display label for one tournament stage; data-driven, never hardcoded to three groups. */
export function typeCupStageLabel(key: TypeCupStageKey, groupCount: number, isLegacy: boolean): string {
  if (isLegacy || key.tournamentPhase === 0) {
    return 'Single field';
  }
  if (key.tournamentPhase === 2) {
    return 'Type Cup Final';
  }
  return qualificationStageLabel(key.qualificationGroup, groupCount);
}

/** Compact navigation label: Group A/B/C or Final. */
export function typeCupStageShortLabel(key: TypeCupStageKey, groupCount: number, isLegacy: boolean): string {
  if (isLegacy || key.tournamentPhase === 0) {
    return 'Single field';
  }
  if (key.tournamentPhase === 2) {
    return 'Final';
  }
  const letter = qualificationGroupLetter(key.qualificationGroup);
  const alias = groupCount === 2 ? (key.qualificationGroup === 1 ? 'Semifinal A' : 'Semifinal B') : null;
  return alias ? `Group ${letter} · ${alias}` : `Group ${letter}`;
}

/** Squad rank-group label inside a stage; never a qualification group. */
export function squadLabel(groupNumber: number): string {
  return rankGroupLabel(groupNumber);
}

/**
 * Stage-local overall round (1-based, chronological) from squad coordinates.
 * Each stage holds squadGroups × roundsPerGroup rounds in canonical order:
 * Squad #1 R1..R8, then Squad #2 R1..R8, etc.
 */
export function stageLocalRound(rankGroup: number | null, roundInGroup: number, roundsPerGroup: number): number {
  const group = rankGroup ?? 1;
  return (group - 1) * roundsPerGroup + roundInGroup;
}

/** Authoritative per-stage total from the backend event shape (squad groups × rounds). */
export function stageLocalTotal(squadGroupCount: number, roundsPerGroup: number): number {
  const total = squadGroupCount * roundsPerGroup;
  return total > 0 ? total : 32;
}

/** Chronological rounds of one stage with unambiguous overall numbers. */
export function chronologicalStageRounds(
  rounds: readonly PlayedRound[],
  stage: TypeCupStageKey,
  roundsPerGroup: number,
): { round: PlayedRound; overall: number }[] {
  return rounds
    .filter(
      (r) => (r.tournamentPhase ?? null) === stage.tournamentPhase && (r.qualificationGroup ?? null) === stage.qualificationGroup,
    )
    .slice()
    .sort((a, b) => (a.group ?? 1) - (b.group ?? 1) || a.round - b.round)
    .map((round) => ({
      round,
      overall: stageLocalRound(round.group ?? 1, round.round, roundsPerGroup),
    }));
}

/**
 * Canonical ordered stages for a Type Cup edition.
 * Data-driven from the authoritative qualification count; never hardcoded.
 */
export function buildTypeCupStages(args: {
  qualificationGroupCount: number;
  isDirectFinal: boolean;
  isLegacy: boolean;
  rounds: readonly PlayedRound[];
  doneQualificationGroups: ReadonlySet<number>;
  finalComplete: boolean;
  activeStage: TypeCupStageKey | null;
  tournamentComplete: boolean;
  squadGroupCount: number;
  roundsPerGroup: number;
}): TypeCupStageInfo[] {
  const total = stageLocalTotal(args.squadGroupCount, args.roundsPerGroup);
  if (args.isLegacy || (args.qualificationGroupCount <= 0 && !args.isDirectFinal)) {
    const key: TypeCupStageKey = { tournamentPhase: 0, qualificationGroup: 0 };
    const played = args.rounds.length;
    return [
      {
        key,
        kind: 'single',
        qualificationGroup: null,
        label: 'Single field',
        shortLabel: 'Single field',
        status: args.tournamentComplete ? 'completed' : args.activeStage && stageKeyEquals(args.activeStage, key) ? 'active' : played >= total ? 'completed' : played > 0 ? 'active' : 'pending',
        played,
        total,
      },
    ];
  }
  if (args.isDirectFinal) {
    const key: TypeCupStageKey = { tournamentPhase: 2, qualificationGroup: 0 };
    const played = args.rounds.length;
    return [
      {
        key,
        kind: 'final',
        qualificationGroup: null,
        label: 'Type Cup Final',
        shortLabel: 'Final',
        status: args.finalComplete || args.tournamentComplete ? 'completed' : args.activeStage && stageKeyEquals(args.activeStage, key) ? 'active' : played >= total ? 'completed' : played > 0 ? 'active' : 'pending',
        played,
        total,
      },
    ];
  }
  const stages: TypeCupStageInfo[] = [];
  for (let qual = 1; qual <= args.qualificationGroupCount; qual += 1) {
    const key: TypeCupStageKey = { tournamentPhase: 1, qualificationGroup: qual };
    const played = args.rounds.filter(
      (r) => (r.tournamentPhase ?? null) === 1 && (r.qualificationGroup ?? null) === qual,
    ).length;
    const done = args.doneQualificationGroups.has(qual) || played >= total;
    const active = !args.tournamentComplete && args.activeStage !== null && stageKeyEquals(args.activeStage, key);
    stages.push({
      key,
      kind: 'qualification',
      qualificationGroup: qual,
      label: typeCupStageLabel(key, args.qualificationGroupCount, false),
      shortLabel: typeCupStageShortLabel(key, args.qualificationGroupCount, false),
      status: done ? 'completed' : active ? 'active' : played > 0 ? 'active' : 'pending',
      played,
      total,
    });
  }
  const finalKey: TypeCupStageKey = { tournamentPhase: 2, qualificationGroup: 0 };
  const finalPlayed = args.rounds.filter(
    (r) => (r.tournamentPhase ?? null) === 2 && (r.qualificationGroup ?? null) === 0,
  ).length;
  const finalActive =
    !args.tournamentComplete && args.activeStage !== null && stageKeyEquals(args.activeStage, finalKey);
  stages.push({
    key: finalKey,
    kind: 'final',
    qualificationGroup: null,
    label: 'Type Cup Final',
    shortLabel: 'Final',
    status: args.finalComplete || args.tournamentComplete ? 'completed' : finalActive ? 'active' : finalPlayed > 0 ? 'active' : 'pending',
    played: finalPlayed,
    total,
  });
  return stages;
}

/** Latest persisted stage, or null when nothing is played yet. */
export function latestPersistedStage(rounds: readonly PlayedRound[]): TypeCupStageKey | null {
  const last = rounds[rounds.length - 1];
  if (!last || last.tournamentPhase === null || last.tournamentPhase === undefined) {
    return null;
  }
  if (last.qualificationGroup === null || last.qualificationGroup === undefined) {
    return null;
  }
  return { tournamentPhase: last.tournamentPhase, qualificationGroup: last.qualificationGroup };
}

/**
 * Canonical playable stage: the backend progress cursor while incomplete,
 * otherwise the latest persisted stage. Null once the tournament is complete.
 */
export function canonicalStageKey(args: {
  complete: boolean;
  progressStage: TypeCupStageKey | null;
  rounds: readonly PlayedRound[];
}): TypeCupStageKey | null {
  if (args.complete) {
    return null;
  }
  if (args.progressStage) {
    return args.progressStage;
  }
  return latestPersistedStage(args.rounds);
}

/**
 * Stage-aware selection: an explicit valid URL stage wins (replay/pending),
 * otherwise follow the canonical stage; when complete with no explicit stage,
 * fall back to the latest persisted stage for replay.
 */
export function selectedStageKey(args: {
  urlStage: TypeCupStageKey | null;
  canonicalStage: TypeCupStageKey | null;
  stages: readonly TypeCupStageInfo[];
  rounds: readonly PlayedRound[];
}): TypeCupStageKey | null {
  if (args.urlStage && args.stages.some((s) => stageKeyEquals(s.key, args.urlStage))) {
    return args.urlStage;
  }
  if (args.canonicalStage) {
    return args.canonicalStage;
  }
  return latestPersistedStage(args.rounds) ?? args.stages[0]?.key ?? null;
}

/** Accessible round-button description coupling overall and squad-local numbers. */
export function stageRoundAccessibleLabel(args: {
  overall: number;
  total: number;
  squadGroup: number | null;
  roundInGroup: number;
  stageLabel: string;
}): string {
  if (args.squadGroup === null) {
    return `${args.stageLabel} Round ${args.overall} of ${args.total}`;
  }
  return `${args.stageLabel} Round ${args.overall} of ${args.total} (${squadLabel(args.squadGroup)} Round ${args.roundInGroup} of 8)`;
}
