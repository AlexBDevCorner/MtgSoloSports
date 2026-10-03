import type { SelectionCandidate, SelectionReport, SelectionTeam } from './selectionApi';

/**
 * Plain-language explanation of a stored Cup selection. Pure and DOM-free:
 * everything here is display wording and display arithmetic over values the
 * backend already persisted; nothing is re-ranked or re-selected.
 */

export interface ComponentShare {
  key: 'bonus' | 'performance' | 'form' | 'prestige';
  label: string;
  weightPermille: number;
  /** Athlete's standing on this component, 0..1000 within the compared field. */
  normThousandths: number;
  /** Rating points this component contributed (weight x standing). */
  contributionThousandths: number;
  /** The measured value behind the standing, formatted for display. */
  raw: string;
}

/** Display-only projection of fixed-point thousandths. */
export function formatRating(thousandths: number): string {
  return (thousandths / 1000).toFixed(3);
}

function percent(permille: number): string {
  return `${Math.round(permille / 10)}%`;
}

export function componentShares(report: SelectionReport, candidate: SelectionCandidate): ComponentShare[] {
  const share = (
    key: ComponentShare['key'],
    label: string,
    weightPermille: number,
    normThousandths: number,
    raw: string,
  ): ComponentShare => ({
    key,
    label,
    weightPermille,
    normThousandths,
    contributionThousandths: Math.floor((normThousandths * weightPermille) / 1000),
    raw,
  });
  return [
    share(
      'bonus',
      'Active bonus',
      report.bonusWeightPermille,
      candidate.bonusNormThousandths,
      `${formatRating(candidate.bonusRawThousandths)}% bonus`,
    ),
    share(
      'performance',
      'Season performance',
      report.performanceWeightPermille,
      candidate.performanceNormThousandths,
      `${formatRating(candidate.performanceRawThousandths)} championship pts`,
    ),
    share(
      'form',
      'Recent form',
      report.formWeightPermille,
      candidate.formNormThousandths,
      `${formatRating(candidate.formRaw)} weighted pts, last 10 stages`,
    ),
    share(
      'prestige',
      'Career prestige',
      report.prestigeWeightPermille,
      candidate.prestigeNormThousandths,
      `${candidate.prestigeRaw} prestige pts`,
    ),
  ];
}

/** "35% bonus + 30% season + 25% form + 10% prestige" from the stored weights. */
export function formulaLabel(report: SelectionReport): string {
  return [
    `${percent(report.bonusWeightPermille)} active bonus`,
    `${percent(report.performanceWeightPermille)} season performance`,
    `${percent(report.formWeightPermille)} recent form`,
    `${percent(report.prestigeWeightPermille)} career prestige`,
  ].join(' + ');
}

export function selectedMembers(team: SelectionTeam): SelectionCandidate[] {
  return team.ranking
    .filter((candidate) => candidate.selected)
    .sort((a, b) => (a.selectionRank ?? 0) - (b.selectionRank ?? 0));
}

/** Reveal order builds suspense: the last squad number first, #1 last. */
export function revealOrder(team: SelectionTeam): SelectionCandidate[] {
  return selectedMembers(team).reverse();
}

/** The best-ranked athlete of this team's ranking who was not selected for it. */
export function firstOut(team: SelectionTeam): SelectionCandidate | null {
  return team.ranking.find((candidate) => !candidate.selected) ?? null;
}

const TIE_ORDER: { label: string; pick: (c: SelectionCandidate) => number }[] = [
  { label: 'active bonus', pick: (c) => c.bonusNormThousandths },
  { label: 'season performance', pick: (c) => c.performanceNormThousandths },
  { label: 'recent form', pick: (c) => c.formNormThousandths },
  { label: 'career prestige', pick: (c) => c.prestigeNormThousandths },
];

/** What separated two athletes level on rating (the documented tie-break order). */
export function tieBreaker(winner: SelectionCandidate, loser: SelectionCandidate): string {
  const decided = TIE_ORDER.find((entry) => entry.pick(winner) !== entry.pick(loser));
  return decided ? decided.label : 'name order';
}

function strongest(shares: ComponentShare[]): ComponentShare {
  return shares.reduce((best, share) => (share.contributionThousandths > best.contributionThousandths ? share : best));
}

function strengthLine(report: SelectionReport, candidate: SelectionCandidate, field: string): string {
  const top = strongest(componentShares(report, candidate));
  const standing = top.normThousandths >= 1000 ? `the best ${top.label.toLowerCase()} of ${field}` : top.raw;
  return `Biggest factor: ${top.label.toLowerCase()} — ${formatRating(top.contributionThousandths)} of the ${formatRating(candidate.finalRatingThousandths)} rating (${standing}).`;
}

function colorLines(report: SelectionReport, team: SelectionTeam, candidate: SelectionCandidate): string[] {
  const field = report.hasFullRanking ? `all ${team.candidateCount} ${team.teamName} athletes` : `${team.teamName}`;
  const lines = [
    report.hasFullRanking
      ? `Ranked #${candidate.rank} of ${team.candidateCount} ${team.teamName} athletes; the top ${report.teamSize} make the squad.`
      : `Ranked #${candidate.rank} in ${team.teamName}; the top ${report.teamSize} make the squad.`,
    strengthLine(report, candidate, field),
  ];
  const out = firstOut(team);
  if (out) {
    const margin = candidate.finalRatingThousandths - out.finalRatingThousandths;
    lines.push(
      margin > 0
        ? `${formatRating(margin)} clear of the cut — first out is ${out.name} (#${out.rank}, ${formatRating(out.finalRatingThousandths)}).`
        : `Level on rating with ${out.name} (#${out.rank}); stayed ahead on ${tieBreaker(candidate, out)}.`,
    );
  }
  return lines;
}

function alternativeList(candidate: SelectionCandidate): string {
  return (candidate.alternatives ?? [])
    .map((alt) => `${alt.teamName} (#${alt.rank}${alt.fieldsTeam ? '' : ', no team this year'})`)
    .join(', ');
}

function reasonLine(team: SelectionTeam, candidate: SelectionCandidate): string | null {
  switch (candidate.reason) {
    case 'Capped':
      return `Capped: already played a Type Cup for ${team.teamName}, so it can never represent another type.`;
    case 'OnlyType':
      return `${team.teamName} is its only creature type with enough athletes to field a team.`;
    case 'BestRank':
      return `Could also represent ${alternativeList(candidate)}; plays for ${team.teamName}, where it ranks highest.`;
    case 'Balanced':
      return `Ranks higher for ${alternativeList(candidate)}, but plays for ${team.teamName} so that as many full teams as possible take part.`;
    default:
      return null;
  }
}

function typeLines(report: SelectionReport, team: SelectionTeam, candidate: SelectionCandidate): string[] {
  const lines = [
    report.hasFullRanking
      ? `Ranked #${candidate.rank} of ${team.candidateCount} eligible ${team.teamName} athletes.`
      : `Ranked #${candidate.rank} among ${team.teamName} athletes.`,
  ];
  const reason = reasonLine(team, candidate);
  if (reason) {
    lines.push(reason);
  }
  const ahead = team.ranking.filter((other) => !other.selected && other.rank < candidate.rank);
  if (ahead.length > 0) {
    const names = ahead
      .map((other) => `${other.name} (${other.assignedTeam ? `plays for ${other.assignedTeam}` : 'not placed'})`)
      .join(', ');
    lines.push(`Called up ahead of higher-ranked ${names}.`);
  }
  lines.push(strengthLine(report, candidate, 'all active athletes'));
  return lines;
}

/** Sentences explaining why a selected athlete is in this team. */
export function explainMember(report: SelectionReport, team: SelectionTeam, candidate: SelectionCandidate): string[] {
  return candidate.reason !== undefined || candidate.assignedTeam !== undefined
    ? typeLines(report, team, candidate)
    : colorLines(report, team, candidate);
}

/** Short status for a ranking row: squad number, or why the athlete is not in this team. */
export function outcomeLabel(team: SelectionTeam, candidate: SelectionCandidate): string {
  if (candidate.selected) {
    return `Selected #${candidate.selectionRank ?? candidate.rank}`;
  }
  if (candidate.assignedTeam) {
    return `Plays for ${candidate.assignedTeam}`;
  }
  if (candidate.assignedTeam === null) {
    return 'Not placed';
  }
  const cut = selectedMembers(team).at(-1);
  if (!cut) {
    return 'Not selected';
  }
  const gap = cut.finalRatingThousandths - candidate.finalRatingThousandths;
  return gap > 0 ? `${formatRating(gap)} short` : `Lost the tie on ${tieBreaker(cut, candidate)}`;
}

/** Reveal progress across the whole selection, e.g. 12 of 32 picks. */
export function revealTotals(report: SelectionReport, revealed: Record<string, number>): { shown: number; total: number } {
  let shown = 0;
  let total = 0;
  for (const team of report.teams) {
    const size = selectedMembers(team).length;
    total += size;
    shown += Math.min(revealed[team.teamKey] ?? 0, size);
  }
  return { shown, total };
}

/** The team whose next pick should be revealed: the active one, else the next unfinished team. */
export function nextRevealTeam(
  report: SelectionReport,
  revealed: Record<string, number>,
  activeKey: string | null,
): SelectionTeam | null {
  const unfinished = (team: SelectionTeam) => (revealed[team.teamKey] ?? 0) < selectedMembers(team).length;
  const active = report.teams.find((team) => team.teamKey === activeKey);
  if (active && unfinished(active)) {
    return active;
  }
  const start = active ? report.teams.indexOf(active) + 1 : 0;
  const ordered = [...report.teams.slice(start), ...report.teams.slice(0, start)];
  return ordered.find(unfinished) ?? null;
}
