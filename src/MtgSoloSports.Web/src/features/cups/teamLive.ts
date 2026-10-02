import type { ColorCupTeamLive, TypeCupTeamLive } from './teamLiveApi';

export interface LiveTeamRow {
  key: string;
  name: string;
  rank: number;
  scoreThousandths: number;
  baseThousandths: number;
  medal: string | null;
}

export interface TeamLiveBoardData {
  kind: 'type' | 'color';
  sourceSeasonNumber: number;
  teamCount: number;
  groupCount: number;
  groupRounds: number;
  completedRounds: number;
  totalRounds: number;
  currentGroupNumber: number;
  currentRoundNumber: number;
  isComplete: boolean;
  isProvisional: boolean;
  championName: string | null;
  lastCompletedGroupNumber: number;
  lastCompletedRoundNumber: number;
  rows: LiveTeamRow[];
}

function medalOrNull(medal: string, isProvisional: boolean): string | null {
  if (isProvisional) {
    return null;
  }
  return medal === 'None' || medal === '' ? null : medal;
}

/**
 * Normalizes the Type Cup live projection for display. Preserves the
 * backend-authoritative row order exactly; React never re-sorts or
 * recomputes sporting totals.
 */
export function typeCupToBoard(live: TypeCupTeamLive): TeamLiveBoardData {
  return {
    kind: 'type',
    sourceSeasonNumber: live.sourceSeasonNumber,
    teamCount: live.teamCount,
    groupCount: live.groupCount,
    groupRounds: live.groupRounds,
    completedRounds: live.completedRounds,
    totalRounds: live.totalRounds,
    currentGroupNumber: live.currentGroupNumber,
    currentRoundNumber: live.currentRoundNumber,
    isComplete: live.isComplete,
    isProvisional: live.isProvisional,
    championName: live.isComplete && live.championCreatureType !== '' ? live.championCreatureType : null,
    lastCompletedGroupNumber: live.lastCompletedGroupNumber,
    lastCompletedRoundNumber: live.lastCompletedRoundNumber,
    rows: live.teams.map((team) => ({
      key: team.creatureType,
      name: team.teamName,
      rank: team.teamRank,
      scoreThousandths: team.teamScoreThousandths,
      baseThousandths: team.teamBaseThousandths,
      medal: medalOrNull(team.medal, live.isProvisional),
    })),
  };
}

/**
 * Normalizes the Color Cup team live projection for display. Same contract
 * as the Type Cup board; the individual event is untouched.
 */
export function colorCupToBoard(live: ColorCupTeamLive): TeamLiveBoardData {
  return {
    kind: 'color',
    sourceSeasonNumber: live.sourceSeasonNumber,
    teamCount: live.teamCount,
    groupCount: live.groupCount,
    groupRounds: live.groupRounds,
    completedRounds: live.completedRounds,
    totalRounds: live.totalRounds,
    currentGroupNumber: live.currentGroupNumber,
    currentRoundNumber: live.currentRoundNumber,
    isComplete: live.isComplete,
    isProvisional: live.isProvisional,
    championName: live.isComplete && live.championTeamName !== '' ? live.championTeamName : null,
    lastCompletedGroupNumber: live.lastCompletedGroupNumber,
    lastCompletedRoundNumber: live.lastCompletedRoundNumber,
    rows: live.teams.map((team) => ({
      key: String(team.sportingColor),
      name: team.teamName,
      rank: team.teamRank,
      scoreThousandths: team.teamScoreThousandths,
      baseThousandths: team.teamBaseThousandths,
      medal: medalOrNull(team.medal, live.isProvisional),
    })),
  };
}

/**
 * Human progress label for the live board header. Display-only; built from
 * authoritative progress counters, never from simulated outcomes.
 */
export function describeCupProgress(board: TeamLiveBoardData): string {
  if (board.isComplete) {
    return `Complete — ${board.completedRounds}/${board.totalRounds} rounds`;
  }
  if (board.completedRounds === 0) {
    return `No rounds yet — Group 1 · 0/${board.totalRounds} rounds`;
  }
  return (
    `Group ${board.currentGroupNumber} · Round ${board.currentRoundNumber}/${board.groupRounds} ` +
    `(${board.completedRounds}/${board.totalRounds} rounds)`
  );
}

/**
 * Stale-response guard for asynchronous live refreshes: only accept a
 * response when it still matches the latest requested save and request id.
 * Pure so quick consecutive round actions or save navigation can never
 * display totals from an older request.
 */
export function isCurrentResponse(
  requestedSaveId: string,
  requestedId: number,
  responseSaveId: string,
  responseId: number,
): boolean {
  return requestedSaveId === responseSaveId && requestedId === responseId;
}
