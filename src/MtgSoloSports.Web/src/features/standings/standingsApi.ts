import { fetchJson } from '../../shared/api/http';

export interface SeasonPlacementAthlete {
  athleteId: number;
  name: string;
  sportingColor: number;
  sportingColorName: string;
  imageUrl: string | null;
  currentEffectiveBonusThousandths: number;
}

export interface SeasonPlacementCell {
  athleteId: number;
  stageNumber: number;
  stageRank: number;
  earnedBonusThousandths: number;
  championshipPointsThousandths: number;
  stageScoreThousandths: number;
  roundWins: number;
}

export interface SeasonPlacements {
  saveId: string;
  seasonNumber: number;
  leagueId: number;
  leagueName: string;
  leagueKind: string;
  feederDivision?: number | null;
  leagueLevel?: string | null;
  isSeasonComplete: boolean;
  completedStages: number;
  /** Authoritative season length from the save-owned rules snapshot (32 in current rules). */
  stagesPerSeason?: number | null;
  /** Authoritative league field size from the save-owned rules snapshot (32 in current rules). */
  leagueSize?: number | null;
  /** Best single-stage championship award in thousandths (77_000 in current rules). */
  maxStagePointsThousandths?: number | null;
  /** Worst single-stage championship award in thousandths (1_000 in current rules). */
  minStagePointsThousandths?: number | null;
  athletes: SeasonPlacementAthlete[];
  placements: SeasonPlacementCell[];
}

export async function fetchSeasonPlacements(
  saveId: string,
  seasonNumber: number,
  leagueId: number,
  signal?: AbortSignal,
): Promise<SeasonPlacements> {
  return fetchJson<SeasonPlacements>(
    `/api/saves/${saveId}/history/seasons/${seasonNumber}/competitions/${leagueId}/placements`,
    { signal },
  );
}
