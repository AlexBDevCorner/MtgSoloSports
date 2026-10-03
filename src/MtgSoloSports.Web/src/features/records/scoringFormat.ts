import type { ScoringRecord, ScoringRecordHolder } from './recordsApi';

/** Display-only context for one scoring holder (no sporting math). */
export function formatScoringContext(holder: ScoringRecordHolder): string {
  const parts: string[] = [`Season ${holder.seasonNumber}`, holder.competition];
  if (holder.leagueName && holder.leagueName !== holder.competition) {
    parts.push(holder.leagueName);
  }
  if (holder.groupNumber !== null && holder.groupNumber !== undefined) {
    parts.push(`Group ${holder.groupNumber}`);
  }
  if (holder.stageNumber !== null && holder.stageNumber !== undefined) {
    parts.push(`Stage ${holder.stageNumber}`);
  }
  if (holder.roundNumber !== null && holder.roundNumber !== undefined) {
    parts.push(`Round ${holder.roundNumber}`);
  }
  return parts.join(' · ');
}

export function groupScoringRecords(records: ScoringRecord[]): {
  league: ScoringRecord[];
  individual: ScoringRecord[];
  team: ScoringRecord[];
} {
  return {
    league: records.filter((r) => r.category === 'League'),
    individual: records.filter((r) => r.category === 'Individual Cups'),
    team: records.filter((r) => r.category === 'Team Cups'),
  };
}
