import type { ChaosCupEdition } from '../cups/chaosCupApi';

/**
 * Pure Chaos Cup Live navigation helpers (MSS-109). No sporting math and no
 * I/O: stage labels, canonical/selected stage and group resolution, and the
 * P16/P17 cut wording. The backend stays authoritative for draws, rounds and
 * standings; this model only decides what a stable `?event=chaos-cup`
 * URL names.
 */

export const CHAOS_STAGE_COUNT = 6;
export const CHAOS_GROUP_SIZE = 32;
export const CHAOS_ADVANCING = 16;
export const CHAOS_ROUNDS_PER_GROUP = 16;
const STAGE_GROUP_COUNTS = [32, 16, 8, 4, 2, 1] as const;

export function chaosGroupCount(stageIndex: number): number {
  if (!Number.isInteger(stageIndex) || stageIndex < 0 || stageIndex >= STAGE_GROUP_COUNTS.length) {
    throw new RangeError(`Chaos stage ${stageIndex} is out of range (0..5).`);
  }
  return STAGE_GROUP_COUNTS[stageIndex]!;
}

export function isFinalChaosStage(stageIndex: number): boolean {
  return chaosGroupCount(stageIndex) === 1;
}

function entrantsForStage(stageIndex: number): number {
  return chaosGroupCount(stageIndex) * CHAOS_GROUP_SIZE;
}

/** "Stage 1 · 32 groups · 1024 athletes" or "Final · 1 group · 32 athletes". */
export function chaosStageLabel(stageIndex: number): string {
  const groups = chaosGroupCount(stageIndex);
  if (isFinalChaosStage(stageIndex)) {
    return `Final · 1 group · ${CHAOS_GROUP_SIZE} athletes`;
  }
  return `Stage ${stageIndex + 1} · ${groups} groups · ${entrantsForStage(stageIndex)} athletes`;
}

/** Compact strip label: "S1 · 32" … "Final". */
export function chaosStageShortLabel(stageIndex: number): string {
  if (isFinalChaosStage(stageIndex)) {
    return 'Final';
  }
  return `S${stageIndex + 1} · ${chaosGroupCount(stageIndex)}`;
}

export type ChaosStageStatus = 'completed' | 'active' | 'notDrawn';

export function chaosStageStatus(state: string, isDrawn: boolean, isComplete: boolean): ChaosStageStatus {
  if (!isDrawn || state === 'NotDrawn') {
    return 'notDrawn';
  }
  return isComplete ? 'completed' : 'active';
}

export function chaosStageStatusLabel(status: ChaosStageStatus): string {
  return status === 'completed' ? 'Completed' : status === 'active' ? 'Active' : 'Not drawn';
}

/**
 * Canonical stage: the first drawn but incomplete stage while the edition is
 * in progress, otherwise the last drawn stage (so a completed edition replays
 * the Final). Null when nothing is drawn yet.
 */
export function canonicalChaosStage(edition: ChaosCupEdition | null): number | null {
  if (!edition) {
    return null;
  }
  const drawn = edition.stages.filter((stage) => stage.isDrawn);
  if (drawn.length === 0) {
    return null;
  }
  const pending = drawn.find((stage) => !stage.isComplete);
  return (pending ?? drawn[drawn.length - 1]!).stageIndex;
}

/**
 * Selected stage: an explicit valid URL stage wins (even undrawn, so the
 * locked state renders instead of silently substituting); otherwise follow
 * the canonical stage, falling back to stage 0.
 */
export function selectedChaosStage(urlStage: number | null, edition: ChaosCupEdition | null): number {
  if (urlStage !== null && Number.isInteger(urlStage) && urlStage >= 0 && urlStage < CHAOS_STAGE_COUNT) {
    return urlStage;
  }
  return canonicalChaosStage(edition) ?? 0;
}

/** Selected group: an explicit in-range URL group wins, otherwise group 1. */
export function selectedChaosGroup(urlGroup: number | null, stageIndex: number): number {
  const count = chaosGroupCount(stageIndex);
  if (urlGroup !== null && Number.isInteger(urlGroup) && urlGroup >= 1 && urlGroup <= count) {
    return urlGroup;
  }
  return 1;
}

/**
 * Selected round: an explicit 1..16 URL round wins (the fetch surfaces 404
 * for an unplayed round); otherwise follow the latest played round, or null
 * when the group holds no rounds yet.
 */
export function selectedChaosRound(urlRound: number | null, roundsPlayed: number | null): number | null {
  if (urlRound !== null && Number.isInteger(urlRound) && urlRound >= 1 && urlRound <= CHAOS_ROUNDS_PER_GROUP) {
    return urlRound;
  }
  if (roundsPlayed === null || roundsPlayed <= 0) {
    return null;
  }
  return Math.min(roundsPlayed, CHAOS_ROUNDS_PER_GROUP);
}

/** "Cut — top 16 advance (provisional)" until the 16th round, then official. */
export function chaosCutLabel(isProvisional: boolean): string {
  return isProvisional ? 'Cut — top 16 advance (provisional)' : 'Cut — top 16 advance (official)';
}

/** Display-only thousandths projection (no sporting math). */
export function formatChaosPoints(thousandths: number): string {
  return (thousandths / 1000).toFixed(3);
}
