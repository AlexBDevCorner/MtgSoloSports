import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import type { SelectionCandidate, SelectionReport, SelectionTeam } from './selectionApi';
import {
  componentShares,
  explainMember,
  firstOut,
  formulaLabel,
  leagueCell,
  nextRevealTeam,
  outcomeLabel,
  revealOrder,
  revealTotals,
  tieBreaker,
  tierLabel,
  tierLines,
} from './selectionExplain.ts';

const here = dirname(fileURLToPath(import.meta.url));
const view = readFileSync(join(here, 'CupSelectionView.tsx'), 'utf8');
const app = readFileSync(join(here, '..', '..', 'App.tsx'), 'utf8');
const flow = readFileSync(join(here, '..', 'dashboard', 'SeasonFlow.tsx'), 'utf8');

function candidate(overrides: Partial<SelectionCandidate>): SelectionCandidate {
  return {
    athleteId: 1,
    name: 'Athlete',
    imageUrl: null,
    rank: 1,
    selected: true,
    selectionRank: 1,
    finalRatingThousandths: 1000,
    bonusNormThousandths: 1000,
    performanceNormThousandths: 1000,
    formNormThousandths: 1000,
    prestigeNormThousandths: 1000,
    bonusRawThousandths: 0,
    performanceRawThousandths: 0,
    formRaw: 0,
    prestigeRaw: 0,
    ...overrides,
  };
}

function report(teams: SelectionTeam[], overrides: Partial<SelectionReport> = {}): SelectionReport {
  return {
    saveId: 'save',
    sourceSeasonNumber: 3,
    rulesVersion: 1,
    hasFullRanking: true,
    teamSize: 4,
    bonusWeightPermille: 350,
    performanceWeightPermille: 300,
    formWeightPermille: 250,
    prestigeWeightPermille: 100,
    teams,
    ...overrides,
  };
}

const red: SelectionTeam = {
  teamKey: 'Red',
  teamName: 'Red',
  candidateCount: 256,
  ranking: [
    candidate({ athleteId: 1, name: 'Ace', rank: 1, selectionRank: 1, finalRatingThousandths: 900 }),
    candidate({ athleteId: 2, name: 'Bee', rank: 2, selectionRank: 2, finalRatingThousandths: 800 }),
    candidate({ athleteId: 3, name: 'Cat', rank: 3, selectionRank: 3, finalRatingThousandths: 700 }),
    candidate({
      athleteId: 4,
      name: 'Dog',
      rank: 4,
      selectionRank: 4,
      finalRatingThousandths: 600,
      bonusNormThousandths: 500,
      performanceNormThousandths: 1000,
      formNormThousandths: 400,
      prestigeNormThousandths: 250,
    }),
    candidate({ athleteId: 5, name: 'Eel', rank: 5, selected: false, selectionRank: null, finalRatingThousandths: 566 }),
    candidate({
      athleteId: 6,
      name: 'Fox',
      rank: 6,
      selected: false,
      selectionRank: null,
      finalRatingThousandths: 566,
      bonusNormThousandths: 900,
    }),
  ],
};

describe('selection explanation', () => {
  it('splits a rating into weighted parts', () => {
    const shares = componentShares(report([red]), red.ranking[3]!);
    assert.deepEqual(
      shares.map((share) => [share.key, share.weightPermille, share.contributionThousandths]),
      [
        ['bonus', 350, 175],
        ['performance', 300, 300],
        ['form', 250, 100],
        ['prestige', 100, 25],
      ],
    );
    assert.equal(
      formulaLabel(report([red])),
      '35% active bonus + 30% season performance + 25% recent form + 10% career prestige',
    );
  });

  it('explains a color pick by rank, biggest factor and margin over the first athlete out', () => {
    const lines = explainMember(report([red]), red, red.ranking[3]!);
    assert.equal(lines[0], 'Ranked #4 of 256 Red athletes; the top 4 make the squad.');
    assert.equal(
      lines[1],
      'Biggest factor: season performance — 0.300 of the 0.600 rating (the best season performance of all 256 Red athletes).',
    );
    assert.equal(lines[2], '0.034 clear of the cut — first out is Eel (#5, 0.566).');
    assert.equal(firstOut(red)?.name, 'Eel');
  });

  it('names the tie-break when the cut is level on rating', () => {
    const level: SelectionTeam = {
      ...red,
      ranking: red.ranking.map((row) =>
        row.athleteId === 4 ? { ...row, finalRatingThousandths: 566 } : row.athleteId === 5 ? { ...row, bonusNormThousandths: 400 } : row,
      ),
    };
    const lines = explainMember(report([level]), level, level.ranking[3]!);
    assert.equal(lines[2], 'Level on rating with Eel (#5); stayed ahead on active bonus.');
    assert.equal(tieBreaker(level.ranking[4]!, { ...level.ranking[4]!, name: 'Zed' }), 'name order');
    assert.equal(outcomeLabel(level, level.ranking[4]!), 'Lost the tie on active bonus');
  });

  it('labels every ranking row', () => {
    assert.equal(outcomeLabel(red, red.ranking[0]!), 'Selected #1');
    assert.equal(outcomeLabel(red, red.ranking[4]!), '0.034 short');
  });

  it('omits field sizes when the selection has no stored ranking', () => {
    const legacy = report([{ ...red, candidateCount: 0, ranking: red.ranking.slice(0, 4) }], { hasFullRanking: false });
    const lines = explainMember(legacy, legacy.teams[0]!, legacy.teams[0]!.ranking[0]!);
    assert.equal(lines[0], 'Ranked #1 in Red; the top 4 make the squad.');
    assert.equal(lines.length, 2, 'no margin without a first athlete out');
  });
});

describe('type cup explanation', () => {
  const wizard: SelectionTeam = {
    teamKey: 'Wizard',
    teamName: 'Wizard',
    candidateCount: 6,
    ranking: [
      candidate({
        athleteId: 1,
        name: 'Ace',
        rank: 1,
        selected: false,
        selectionRank: null,
        assignedTeam: 'Human',
        capped: true,
        reason: null,
        alternatives: [],
      }),
      candidate({
        athleteId: 2,
        name: 'Bee',
        rank: 2,
        selectionRank: 1,
        assignedTeam: 'Wizard',
        capped: true,
        reason: 'Capped',
        alternatives: [],
      }),
      candidate({
        athleteId: 3,
        name: 'Cat',
        rank: 3,
        selectionRank: 2,
        assignedTeam: 'Wizard',
        capped: false,
        reason: 'OnlyType',
        alternatives: [],
      }),
      candidate({
        athleteId: 4,
        name: 'Dog',
        rank: 4,
        selectionRank: 3,
        assignedTeam: 'Wizard',
        capped: false,
        reason: 'BestRank',
        alternatives: [{ teamName: 'Human', rank: 9, fieldsTeam: true }],
      }),
      candidate({
        athleteId: 5,
        name: 'Eel',
        rank: 5,
        selectionRank: 4,
        assignedTeam: 'Wizard',
        capped: false,
        reason: 'Balanced',
        alternatives: [
          { teamName: 'Elf', rank: 2, fieldsTeam: true },
          { teamName: 'Sphinx', rank: 1, fieldsTeam: false },
        ],
      }),
      candidate({
        athleteId: 6,
        name: 'Fox',
        rank: 6,
        selected: false,
        selectionRank: null,
        assignedTeam: null,
        capped: false,
        reason: null,
        alternatives: [],
      }),
    ],
  };
  const data = report([wizard]);

  it('gives the reason each member represents the type', () => {
    assert.equal(
      explainMember(data, wizard, wizard.ranking[1]!)[1],
      'Capped: already played a Type Cup for Wizard, so it can never represent another type.',
    );
    assert.equal(
      explainMember(data, wizard, wizard.ranking[2]!)[1],
      'Wizard is its only creature type with enough athletes to field a team.',
    );
    assert.equal(
      explainMember(data, wizard, wizard.ranking[3]!)[1],
      'Could also represent Human (#9); plays for Wizard, where it ranks highest.',
    );
    assert.equal(
      explainMember(data, wizard, wizard.ranking[4]!)[1],
      'Ranks higher for Elf (#2), Sphinx (#1, no team this year), but plays for Wizard so that as many full teams as possible take part.',
    );
  });

  it('says who ranked higher but plays elsewhere', () => {
    const lines = explainMember(data, wizard, wizard.ranking[1]!);
    assert.equal(lines[0], 'Ranked #2 of 6 eligible Wizard athletes.');
    assert.equal(lines[2], 'Called up ahead of higher-ranked Ace (plays for Human).');
    assert.equal(outcomeLabel(wizard, wizard.ranking[0]!), 'Plays for Human');
    assert.equal(outcomeLabel(wizard, wizard.ranking[5]!), 'Not placed');
    assert.equal(outcomeLabel(wizard, wizard.ranking[4]!), 'Selected #4');
  });
});

describe('selection reveal', () => {
  const blue: SelectionTeam = { ...red, teamKey: 'Blue', teamName: 'Blue' };
  const data = report([red, blue]);

  it('reveals the last squad number first and #1 last', () => {
    assert.deepEqual(
      revealOrder(red).map((member) => member.selectionRank),
      [4, 3, 2, 1],
    );
  });

  it('counts revealed picks across teams', () => {
    assert.deepEqual(revealTotals(data, {}), { shown: 0, total: 8 });
    assert.deepEqual(revealTotals(data, { Red: 4, Blue: 1 }), { shown: 5, total: 8 });
    assert.deepEqual(revealTotals(data, { Red: 9 }), { shown: 4, total: 8 });
  });

  it('stays on the active team until it is complete, then moves on', () => {
    assert.equal(nextRevealTeam(data, {}, 'Blue')?.teamKey, 'Blue');
    assert.equal(nextRevealTeam(data, { Red: 4 }, 'Red')?.teamKey, 'Blue');
    assert.equal(nextRevealTeam(data, { Blue: 4 }, 'Blue')?.teamKey, 'Red');
    assert.equal(nextRevealTeam(data, { Red: 4, Blue: 4 }, 'Red'), null);
  });
});

describe('selection is an event on Live', () => {
  it('announces through the backend once and reveals only the stored report', () => {
    assert.ok(view.includes('announceSelection(saveId, selection, season)'));
    assert.ok(view.includes('fetchSelectionReport(saveId, selection, season'));
    assert.ok(view.includes('if (busy)'), 'duplicate submissions are blocked');
    assert.ok(view.includes('onMutated()'), 'dashboard status refreshes after the selection');
    for (const token of ['Announce the squads', 'Reveal next pick', 'Reveal team', 'Reveal all', 'Replay the reveal']) {
      assert.ok(view.includes(token), token);
    }
    assert.ok(view.includes('Not the next event'), 'a selection that is not next cannot be announced');
  });

  it('hides the ranking until the whole squad is revealed', () => {
    assert.ok(view.includes('complete ? ('));
    assert.ok(view.includes('who just missed out'));
  });

  it('is reachable from the URL, the next lifecycle step and the Dashboard', () => {
    assert.ok(app.includes('<CupSelectionView'));
    assert.ok(app.includes('route.selection ?? (route.event ? null : pendingSelection)'));
    assert.ok(flow.includes('Announce on Live'));
    assert.ok(flow.includes('livePath(saveId, { event: next.liveSelection, season: flow.seasonNumber })'));
    assert.ok(flow.includes('Select now'), 'the one-click path stays available');
  });
});

describe('tier-aware selection explanation (MSS-066)', () => {
  it('labels every source tier including the pool', () => {
    assert.equal(tierLabel(0), 'Superleague');
    assert.equal(tierLabel(1), 'Feeder 1');
    assert.equal(tierLabel(2), 'Feeder 2');
    assert.equal(tierLabel(3), 'Feeder 3');
    assert.equal(tierLabel(null), 'Pool');
    assert.equal(tierLabel(undefined), 'Pool');
  });

  it('stays silent for legacy candidates without tier data', () => {
    assert.deepEqual(tierLines(candidate({})), []);
  });

  it('explains the source league, factor and adjusted inputs', () => {
    const lines = tierLines(
      candidate({
        sourceLeagueName: 'Red League F2',
        sourceLeagueLevel: 2,
        strengthFactorPermille: 600,
        unadjustedPerformanceThousandths: 10000,
        unadjustedFormAggregate: 55000,
        performanceRawThousandths: 6000,
        formRaw: 33000,
        prestigeRaw: 200,
        prestigeFeeder2TitleRaw: 200,
        prestigeSuperTitleRaw: 0,
        prestigeFeeder1TitleRaw: 0,
        prestigeFeeder3TitleRaw: 0,
        prestigeAppearanceRaw: 0,
        prestigeSuperStageRaw: 0,
        prestigeFeeder1StageRaw: 0,
        prestigeFeeder2StageRaw: 0,
        prestigeFeeder3StageRaw: 0,
        prestigeMajorCupRaw: 0,
      }),
    );
    assert.equal(lines.length, 2);
    assert.ok(lines[0]!.includes('Red League F2'));
    assert.ok(lines[0]!.includes('Feeder 2'));
    assert.ok(lines[0]!.includes('×0.60'));
    assert.ok(lines[0]!.includes('10.000 pts → 6.000 adjusted'));
    assert.ok(lines[1]!.includes('Career prestige 200 pts'));
    assert.ok(lines[1]!.includes('F2 titles 200'));
  });

  it('gives pool athletes no stale-form advantage', () => {
    const lines = tierLines(
      candidate({ sourceLeagueName: 'Pool', sourceLeagueLevel: null, strengthFactorPermille: 0 }),
    );
    assert.equal(lines.length, 1);
    assert.ok(lines[0]!.includes('common pool'));
    assert.ok(lines[0]!.includes('score 0.000'));
  });

  it('shows the league in ranking cells with the factor as hover text', () => {
    const cell = leagueCell(candidate({ sourceLeagueName: 'Blue Superleague', sourceLeagueLevel: 0, strengthFactorPermille: 1000 }));
    assert.equal(cell.text, 'Blue Superleague · Superleague');
    assert.ok(cell.title.includes('×1.00'));
    assert.equal(leagueCell(candidate({})).text, 'Pool');
  });

  it('appends tier context after the existing why-selected lines', () => {
    const team: SelectionTeam = {
      teamKey: 'Red',
      teamName: 'Red',
      candidateCount: 256,
      ranking: [
        candidate({
          athleteId: 1,
          name: 'Ace',
          rank: 1,
          selectionRank: 1,
          finalRatingThousandths: 900,
          sourceLeagueName: 'Red League F2',
          sourceLeagueLevel: 2,
          strengthFactorPermille: 600,
          unadjustedPerformanceThousandths: 10000,
          unadjustedFormAggregate: 55000,
          performanceRawThousandths: 6000,
          formRaw: 33000,
        }),
      ],
    };
    const lines = explainMember(report([team]), team, team.ranking[0]!);
    assert.ok(lines[0]!.startsWith('Ranked #1'));
    assert.ok(lines.some((line) => line.includes('Red League F2')));
  });

  it('renders the league column and the strength help', () => {
    assert.ok(view.includes('<th scope="col">League</th>'));
    assert.ok(view.includes('leagueCell(row)'));
    assert.ok(view.includes('Superleague ×1.00'));
  });
});
