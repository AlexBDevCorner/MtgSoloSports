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
