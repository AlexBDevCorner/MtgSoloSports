import type { ChaosCupEdition, ChaosCupGroup, ChaosCupSurvival } from './chaosCupApi';
import { chaosGroupCount, isFinalChaosStage } from '../live/chaosCupLiveModel';

/**
 * Pure helpers for the read-only Chaos Cup edition page (MSS-112). No I/O and
 * no sporting math: stage-strip labels, draw-rule wording, group-number
 * expansion and the official-Final gate. The backend stays authoritative for
 * draws, standings and survival counts; this model only decides what the
 * page may show without inventing a champion or a 1024-row table.
 */

export const CHAOS_STAGE_ENTRANTS = [1024, 512, 256, 128, 64, 32] as const;

/** "1024→512→256→128→64→32" tournament strip for the banner. */
export function chaosStageStrip(): string {
  return [...CHAOS_STAGE_ENTRANTS].join('→');
}

/** Entrants for a stage (1024, 512, 256, 128, 64, 32). */
export function chaosEntrantsForStage(stageIndex: number): number {
  if (!Number.isInteger(stageIndex) || stageIndex < 0 || stageIndex >= CHAOS_STAGE_ENTRANTS.length) {
    throw new RangeError(`Chaos stage ${stageIndex} is out of range (0..5).`);
  }
  return CHAOS_STAGE_ENTRANTS[stageIndex]!;
}

/** 1-based group numbers for a stage (1..32 for stage 0, [1] for the Final). */
export function chaosGroupNumbers(stageIndex: number): number[] {
  const count = chaosGroupCount(stageIndex);
  return Array.from({ length: count }, (_, index) => index + 1);
}

/**
 * Draw-rule wording. The first draw balances every group to exactly
 * 4 athletes per sporting color; later draws are pure random with no
 * color or tier seeding.
 */
export function chaosDrawRuleLabel(stageIndex: number): string {
  if (isFinalChaosStage(stageIndex)) {
    return 'Final draw: pure random from the 64 survivors, no color or tier seeding.';
  }
  if (stageIndex === 0) {
    return 'First draw: exactly 4 per sporting color in every group.';
  }
  return 'Later draws: pure random, no color or tier seeding.';
}

/** Short stage state for the strip: Completed, Active, Not drawn. */
export function chaosEditionStageStatus(isDrawn: boolean, isComplete: boolean): string {
  if (!isDrawn) {
    return 'Not drawn';
  }
  return isComplete ? 'Completed' : 'Active';
}

/**
 * Official-Final gate. The Final table, podium and champion render only when
 * the edition is complete and the Final group holds its official 32 places.
 * Partial editions never invent a champion.
 */
export function isFinalOfficial(edition: ChaosCupEdition | null, finalGroup: ChaosCupGroup | null): boolean {
  if (!edition || !edition.isComplete) {
    return false;
  }
  if (!finalGroup || !finalGroup.isComplete || finalGroup.standingsProvisional) {
    return false;
  }
  if (finalGroup.standings.length !== 32) {
    return false;
  }
  return finalGroup.standings.every((row, index) => row.groupRank === index + 1);
}

/** Survival stage by index, or null when the backend has no row yet. */
export function survivalStageFor(
  survival: ChaosCupSurvival | null,
  stageIndex: number,
): ChaosCupSurvival['stages'][number] | null {
  if (!survival) {
    return null;
  }
  return survival.stages.find((stage) => stage.stageIndex === stageIndex) ?? null;
}
