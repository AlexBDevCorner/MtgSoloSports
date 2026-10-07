import { fetchJson } from '../../shared/api/http';

export interface AthleteCard {
  name: string;
  sportingColor: number;
  sportingColorName: string;
  creatureTypes: string[];
  frontColors: string;
  manaCost: string;
  typeLine: string;
  imageUrl: string | null;
  setCode: string | null;
  isArtifact: boolean;
  hasDevoid: boolean;
  hasHybridMana: boolean;
  typeCupNationality?: string | null;
}

export interface AthleteCareer {
  seasonsActive: number;
  isActive: boolean;
  currentLeagueId: number | null;
  currentLeagueName: string | null;
  currentLeagueKind: number | null;
  roundWins: number;
  stageWins: number;
  stageSeconds: number;
  stageThirds: number;
  stagePodiums: number;
  bestSeasonFinish: number | null;
  bestSeasonNumber: number | null;
  lifetimeEarnedBonusThousandths: number;
  currentEffectiveBonusThousandths: number;
  lastSeasonNumber: number;
  lastStageNumber: number;
}

export interface AthleteSeason {
  seasonNumber: number;
  seasonId: number;
  wasActive: boolean;
  leagueId: number | null;
  leagueName: string | null;
  leagueKind: number | null;
  /** Tier identity ("Superleague", "Feeder1", ...); null for pool seasons. */
  leagueLevel?: string | null;
  /** Persisted feeder division (0 legacy v1); null for pool seasons. */
  feederDivision?: number | null;
  roundWins: number;
  stageWins: number;
  stageSeconds: number;
  stageThirds: number;
  seasonRank: number | null;
  isChampion: boolean;
  earnedBonusThousandths: number;
  totalChampionshipPointsThousandths: number;
  totalStageScoreThousandths: number;
  totalBaseScoreThousandths: number;
}

export interface AthleteHonour {
  seasonNumber: number;
  leagueName: string;
  honourKind: string;
}

export interface AthleteMovement {
  fromSeasonNumber: number;
  toSeasonNumber: number;
  fromLeagueName: string;
  toLeagueName: string;
  kind: string;
  fromSeasonRank: number;
  /** Adjacent-tier source/destination; null for the common pool. */
  fromLeagueLevel?: string | null;
  toLeagueLevel?: string | null;
}

export interface AthleteCupSelection {
  cupKind: string;
  sourceSeasonNumber: number;
  team: string;
  selectionRank: number;
}

export interface AthleteCupHistory {
  sourceSeasonNumber: number;
  cup: string;
  event: string;
  eventName: string;
  teamKey: string;
  teamName: string;
  place: number;
  medal: string;
  scoreThousandths: number;
  groupRank: number | null;
  groupNumber: number | null;
  /** Type Cup tournament stage (MSS-063 additive); null for Color Cup entries. */
  tournamentPhase?: number | null;
  qualificationGroup?: number | null;
  tournamentStage?: string | null;
}

export interface AthleteProfile {
  saveId: string;
  athleteId: number;
  card: AthleteCard;
  career: AthleteCareer;
  seasons: AthleteSeason[];
  honours: AthleteHonour[];
  movements: AthleteMovement[];
  cupSelections: AthleteCupSelection[];
  cupHistory: AthleteCupHistory[];
}

export interface AthleteRecordHolding {
  recordKey: string;
  label: string;
  value: number;
  valueDisplay: string;
  isBonus: boolean;
}

export interface AthleteRecordHoldings {
  saveId: string;
  athleteId: number;
  holdings: AthleteRecordHolding[];
}

export interface CurrentStandingRow {
  athleteId: number;
  name: string;
  seasonRank: number;
  totalChampionshipPointsThousandths: number;
  totalStageScoreThousandths: number;
  totalBaseScoreThousandths: number;
  stageWins: number;
  roundWins: number;
  isChampion: boolean;
  sportingColor: number;
  sportingColorName: string;
  imageUrl: string | null;
}

export interface CurrentStandings {
  saveId: string;
  seasonNumber: number;
  leagueId: number;
  leagueName: string;
  completedStages: number;
  globalStage: number;
  isSeasonComplete: boolean;
  isFinal: boolean;
  seasonChecksum: string;
  standings: CurrentStandingRow[];
}

export async function fetchAthleteProfile(
  saveId: string,
  athleteId: number,
  signal?: AbortSignal,
): Promise<AthleteProfile> {
  return fetchJson<AthleteProfile>(`/api/saves/${saveId}/athletes/${athleteId}`, {
    signal,
  });
}

export async function fetchAthleteRecordHoldings(
  saveId: string,
  athleteId: number,
  signal?: AbortSignal,
): Promise<AthleteRecordHoldings> {
  return fetchJson<AthleteRecordHoldings>(
    `/api/saves/${saveId}/athletes/${athleteId}/record-holdings`,
    { signal },
  );
}

export async function fetchCurrentStandings(
  saveId: string,
  leagueId: number,
  signal?: AbortSignal,
): Promise<CurrentStandings> {
  return fetchJson<CurrentStandings>(
    `/api/saves/${saveId}/leagues/${leagueId}/standings/current`,
    { signal },
  );
}
