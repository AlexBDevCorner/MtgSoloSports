/**
 * Pure matrix helpers for the MSS-041 standings page.
 * No sporting math here: placements are persisted history, totals come from
 * the authoritative season table / current standings. These helpers only
 * align, sort and format for display.
 */

import type { SeasonPlacementCell } from './standingsApi';

export type MatrixSortKey = 'rank' | 'wins' | 'points';

export interface MatrixRowTotals {
  athleteId: number;
  seasonRank: number | null;
  totalChampionshipPointsThousandths: number | null;
  stageWins: number | null;
  roundWins: number | null;
  name: string | null;
}

export interface CombinedMatrixRow {
  athleteId: number;
  name: string;
  seasonRank: number | null;
  totalChampionshipPointsThousandths: number | null;
  stageWins: number | null;
  roundWins: number | null;
  sportingColorName: string;
  imageUrl: string | null;
  currentEffectiveBonusThousandths: number;
}

/** Display-only projection of a fixed-point thousandths value (no sporting math). */
export function formatPoints(thousandths: number): string {
  return (thousandths / 1000).toFixed(3);
}

/** Display-only bonus with explicit sign (no sporting math). */
export function formatBonus(thousandths: number): string {
  const sign = thousandths >= 0 ? '+' : '';
  return `${sign}${(thousandths / 1000).toFixed(3)}`;
}

export function placeLabel(stageRank: number): string {
  return `P${stageRank}`;
}

export function podiumClass(stageRank: number | null): string {
  if (stageRank === null || stageRank === undefined) {
    return 'place-empty';
  }
  if (stageRank === 1) {
    return 'place-win';
  }
  if (stageRank === 2 || stageRank === 3) {
    return 'place-podium';
  }
  if (stageRank <= 10) {
    return 'place-top10';
  }
  return 'place-rest';
}

export function buildCellMap(
  placements: ReadonlyArray<SeasonPlacementCell>,
): Map<number, Map<number, SeasonPlacementCell>> {
  const byAthlete = new Map<number, Map<number, SeasonPlacementCell>>();
  for (const cell of placements) {
    let byStage = byAthlete.get(cell.athleteId);
    if (!byStage) {
      byStage = new Map<number, SeasonPlacementCell>();
      byAthlete.set(cell.athleteId, byStage);
    }
    // Stage places are immutable history: first write wins if duplicated.
    if (!byStage.has(cell.stageNumber)) {
      byStage.set(cell.stageNumber, cell);
    }
  }
  return byAthlete;
}

export function completedStageNumbers(
  placements: ReadonlyArray<SeasonPlacementCell>,
): number[] {
  const seen = new Set<number>();
  for (const cell of placements) {
    seen.add(cell.stageNumber);
  }
  return [...seen].sort((a, b) => a - b);
}

export function maxCompletedStage(placements: ReadonlyArray<SeasonPlacementCell>): number | null {
  let max: number | null = null;
  for (const cell of placements) {
    if (max === null || cell.stageNumber > max) {
      max = cell.stageNumber;
    }
  }
  return max;
}

/**
 * Sort combined rows for both the season table and the matrix.
 * Rank is the default stable order; wins/points reorder rows while stage
 * columns stay chronological (1..32 left to right).
 */
export function sortMatrixRows<T extends CombinedMatrixRow>(
  rows: ReadonlyArray<T>,
  sortKey: MatrixSortKey,
): T[] {
  const copy = [...rows];
  switch (sortKey) {
    case 'wins':
      copy.sort((a, b) => {
        const winsA = a.stageWins ?? -1;
        const winsB = b.stageWins ?? -1;
        if (winsB !== winsA) {
          return winsB - winsA;
        }
        const pointsA = a.totalChampionshipPointsThousandths ?? -1;
        const pointsB = b.totalChampionshipPointsThousandths ?? -1;
        if (pointsB !== pointsA) {
          return pointsB - pointsA;
        }
        const rankA = a.seasonRank ?? Number.MAX_SAFE_INTEGER;
        const rankB = b.seasonRank ?? Number.MAX_SAFE_INTEGER;
        if (rankA !== rankB) {
          return rankA - rankB;
        }
        return a.name.localeCompare(b.name);
      });
      return copy;
    case 'points':
      copy.sort((a, b) => {
        const pointsA = a.totalChampionshipPointsThousandths ?? -1;
        const pointsB = b.totalChampionshipPointsThousandths ?? -1;
        if (pointsB !== pointsA) {
          return pointsB - pointsA;
        }
        const winsA = a.stageWins ?? -1;
        const winsB = b.stageWins ?? -1;
        if (winsB !== winsA) {
          return winsB - winsA;
        }
        const rankA = a.seasonRank ?? Number.MAX_SAFE_INTEGER;
        const rankB = b.seasonRank ?? Number.MAX_SAFE_INTEGER;
        if (rankA !== rankB) {
          return rankA - rankB;
        }
        return a.name.localeCompare(b.name);
      });
      return copy;
    case 'rank':
    default:
      copy.sort((a, b) => {
        const rankA = a.seasonRank ?? Number.MAX_SAFE_INTEGER;
        const rankB = b.seasonRank ?? Number.MAX_SAFE_INTEGER;
        if (rankA !== rankB) {
          return rankA - rankB;
        }
        return a.name.localeCompare(b.name);
      });
      return copy;
  }
}

/** All 32 stage columns in chronological order (1..32). */
export function allStageColumns(): number[] {
  return Array.from({ length: 32 }, (_, index) => index + 1);
}

export type StageWindow = 'all' | 'completed' | '1-8' | '9-16' | '17-24' | '25-32';

export function visibleStageColumns(
  window: StageWindow,
  placements: ReadonlyArray<SeasonPlacementCell>,
): number[] {
  const all = allStageColumns();
  switch (window) {
    case '1-8':
      return all.slice(0, 8);
    case '9-16':
      return all.slice(8, 16);
    case '17-24':
      return all.slice(16, 24);
    case '25-32':
      return all.slice(24, 32);
    case 'completed': {
      const completed = new Set(completedStageNumbers(placements));
      return all.filter((stage) => completed.has(stage));
    }
    case 'all':
    default:
      return all;
  }
}

export function cellTitle(
  athleteName: string,
  stageNumber: number,
  cell: SeasonPlacementCell | undefined,
): string {
  if (!cell) {
    return `${athleteName} · Stage ${stageNumber}: not yet completed`;
  }
  return (
    `${athleteName} · Stage ${stageNumber}: P${cell.stageRank} · ` +
    `${formatPoints(cell.championshipPointsThousandths)} champ pts · ` +
    `earned bonus ${formatBonus(cell.earnedBonusThousandths)} (active from next stage)`
  );
}
