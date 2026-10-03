import type { EventTeamMember, EventTeamRow } from './eventsApi';

/** One team row of the standings as they stand at the current reveal step. */
export interface RevealedTeamRow {
  teamName: string;
  /** Points the team's revealed athletes took in the round being revealed (thousandths). */
  roundThousandths: number;
  /** Total before the round plus the revealed round points (thousandths). */
  scoreThousandths: number;
}

/**
 * Team standings at one reveal step: each team's persisted total before the
 * round plus the stored round points of its athletes revealed so far. Display
 * only — integer sums over persisted values, sorted by score then team name
 * like the backend's provisional projection; no ranking and no tie-break.
 */
export function projectRevealedTeamStandings(
  before: readonly EventTeamRow[],
  members: readonly EventTeamMember[],
  revealed: readonly { athleteId: number; finalThousandths: number }[],
): RevealedTeamRow[] {
  const teamByAthlete = new Map(members.map((member) => [member.athleteId, member.teamName]));
  const roundPoints = new Map<string, number>();
  for (const placement of revealed) {
    const team = teamByAthlete.get(placement.athleteId);
    if (team !== undefined) {
      roundPoints.set(team, (roundPoints.get(team) ?? 0) + placement.finalThousandths);
    }
  }
  return before
    .map((row) => {
      const roundThousandths = roundPoints.get(row.teamName) ?? 0;
      return { teamName: row.teamName, roundThousandths, scoreThousandths: row.scoreThousandths + roundThousandths };
    })
    .sort((a, b) => b.scoreThousandths - a.scoreThousandths || (a.teamName < b.teamName ? -1 : a.teamName > b.teamName ? 1 : 0));
}
