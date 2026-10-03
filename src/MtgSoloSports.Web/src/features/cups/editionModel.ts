import type { ColorCupTeamResult } from './colorCupApi';
import type { TypeCupTeamResult } from './typeCupApi';
import { teamKeyFromName } from './cupFormat.ts';

/**
 * One shape for a team event result of either Cup, so the edition page does
 * not branch on Color vs Type. Pure mapping of values the backend stored.
 */

export interface EditionTeam {
  teamKey: string;
  teamName: string;
  teamRank: number;
  teamScoreThousandths: number;
  teamBaseThousandths: number;
  groupWins: number;
  roundWins: number;
  medal: string;
}

export interface EditionLeg {
  athleteId: number;
  name: string;
  imageUrl: string | null;
  teamKey: string;
  teamName: string;
  selectionRank: number;
  groupNumber: number;
  groupRank: number;
  groupScoreThousandths: number;
  roundWins: number;
}

export interface EditionTeamResult {
  groupRounds: number;
  checksum: string;
  teams: EditionTeam[];
  legs: EditionLeg[];
}

export function fromColorTeamResult(result: ColorCupTeamResult): EditionTeamResult {
  return {
    groupRounds: result.groupRounds,
    checksum: result.checksum,
    teams: result.teams.map((team) => ({
      teamKey: teamKeyFromName('color', team.teamName),
      teamName: team.teamName,
      teamRank: team.teamRank,
      teamScoreThousandths: team.teamScoreThousandths,
      teamBaseThousandths: team.teamBaseThousandths,
      groupWins: team.groupWins,
      roundWins: team.roundWins,
      medal: team.medal,
    })),
    legs: result.legs.map((leg) => ({
      athleteId: leg.athleteId,
      name: leg.name,
      imageUrl: leg.imageUrl,
      teamKey: teamKeyFromName('color', leg.sportingColor),
      teamName: leg.sportingColor,
      selectionRank: leg.selectionRank,
      groupNumber: leg.groupNumber,
      groupRank: leg.groupRank,
      groupScoreThousandths: leg.groupScoreThousandths,
      roundWins: leg.roundWins,
    })),
  };
}

export function fromTypeTeamResult(result: TypeCupTeamResult): EditionTeamResult {
  return {
    groupRounds: result.groupRounds,
    checksum: result.checksum,
    teams: result.teams.map((team) => ({
      teamKey: team.creatureType,
      teamName: team.teamName,
      teamRank: team.teamRank,
      teamScoreThousandths: team.teamScoreThousandths,
      teamBaseThousandths: team.teamBaseThousandths,
      groupWins: team.groupWins,
      roundWins: team.roundWins,
      medal: team.medal,
    })),
    legs: result.legs.map((leg) => ({
      athleteId: leg.athleteId,
      name: leg.name,
      imageUrl: leg.imageUrl,
      teamKey: leg.creatureType,
      teamName: leg.creatureType,
      selectionRank: leg.selectionRank,
      groupNumber: leg.groupNumber,
      groupRank: leg.groupRank,
      groupScoreThousandths: leg.groupScoreThousandths,
      roundWins: leg.roundWins,
    })),
  };
}

/** Legs split into their rank groups, each in finishing order. */
export function legsByGroup(legs: EditionLeg[]): { groupNumber: number; legs: EditionLeg[] }[] {
  const numbers = [...new Set(legs.map((leg) => leg.groupNumber))].sort((a, b) => a - b);
  return numbers.map((groupNumber) => ({
    groupNumber,
    legs: legs.filter((leg) => leg.groupNumber === groupNumber).sort((a, b) => a.groupRank - b.groupRank),
  }));
}
