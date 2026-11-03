import { describe, it, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { create, act } from 'react-test-renderer';
import { ChaosCupEditionPage } from './ChaosCupEditionPage.tsx';
import { chaosLivePath } from '../routing/routes.ts';

// MSS-112 mounted coverage: completed and partial Chaos editions render the
// 1024→…→32 strip with on-demand groups/survival, the official Final P1..P32
// plus podium only when complete, each group linking to its persisted replay
// and each athlete to its profile, with the P16/P17 cutoff highlighted and
// the first-vs-later draw rules explained. No 1024-row table by default.

(globalThis as unknown as { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
(globalThis as unknown as { React: typeof React }).React = React;

const SAVE = '55555555-5555-5555-5555-555555555555';
const SEASON = 1;

function okJson(data: unknown): unknown {
  return {
    ok: true,
    status: 200,
    statusText: 'OK',
    json: () => Promise.resolve(data),
    text: () => Promise.resolve(JSON.stringify(data)),
  };
}

function notFound(): unknown {
  return {
    ok: false,
    status: 404,
    statusText: 'Not Found',
    json: () => Promise.resolve({ error: 'not found' }),
    text: () => Promise.resolve('not found'),
  };
}

function stageSummary(
  stageIndex: number,
  overrides: Record<string, unknown> = {},
): Record<string, unknown> {
  const groupCounts = [32, 16, 8, 4, 2, 1];
  const entrants = [1024, 512, 256, 128, 64, 32];
  return {
    stageIndex,
    groupCount: groupCounts[stageIndex],
    entrantCount: entrants[stageIndex],
    isFinal: stageIndex === 5,
    state: 'Completed',
    isDrawn: true,
    isComplete: true,
    drawChecksum: `draw-${stageIndex}`,
    groupsCompleted: groupCounts[stageIndex],
    roundsPlayed: (groupCounts[stageIndex] as number) * 16,
    totalRounds: (groupCounts[stageIndex] as number) * 16,
    standingsCount: entrants[stageIndex],
    ...overrides,
  };
}

function completedEdition(): Record<string, unknown> {
  return {
    saveId: SAVE,
    sourceSeasonNumber: SEASON,
    sourceSeasonId: 9,
    rulesVersion: 3,
    chaosFormatVersion: 1,
    fieldSize: 1024,
    editionChecksum: 'ed',
    state: 'Completed',
    isComplete: true,
    stageCount: 6,
    totalGroups: 63,
    totalRounds: 1008,
    roundsPlayed: 1008,
    groupsCompleted: 63,
    stages: [0, 1, 2, 3, 4, 5].map((stage) => stageSummary(stage)),
  };
}

function partialEdition(): Record<string, unknown> {
  return {
    saveId: SAVE,
    sourceSeasonNumber: SEASON,
    sourceSeasonId: 9,
    rulesVersion: 3,
    chaosFormatVersion: 1,
    fieldSize: 1024,
    editionChecksum: 'ed',
    state: 'InProgress',
    isComplete: false,
    stageCount: 6,
    totalGroups: 63,
    totalRounds: 1008,
    roundsPlayed: 16,
    groupsCompleted: 1,
    stages: [
      stageSummary(0, { state: 'InProgress', isComplete: false, roundsPlayed: 16, totalRounds: 512 }),
      ...[1, 2, 3, 4, 5].map((stage) => ({
        stageIndex: stage,
        groupCount: [32, 16, 8, 4, 2, 1][stage],
        entrantCount: [1024, 512, 256, 128, 64, 32][stage],
        isFinal: stage === 5,
        state: 'NotDrawn',
        isDrawn: false,
        isComplete: false,
        drawChecksum: '',
        groupsCompleted: 0,
        roundsPlayed: 0,
        totalRounds: ([32, 16, 8, 4, 2, 1][stage] as number) * 16,
        standingsCount: 0,
      })),
    ],
  };
}

function completedListing(): Record<string, unknown> {
  return {
    saveId: SAVE,
    editions: [
      {
        cup: 'Chaos',
        sourceSeasonNumber: SEASON,
        state: 'Completed',
        teamCount: 1024,
        podium: [
          { teamKey: '101', teamName: 'Alice', teamRank: 1, medal: 'Gold', teamScoreThousandths: 41000 },
          { teamKey: '102', teamName: 'Bob', teamRank: 2, medal: 'Silver', teamScoreThousandths: 40000 },
          { teamKey: '103', teamName: 'Cara', teamRank: 3, medal: 'Bronze', teamScoreThousandths: 39000 },
        ],
        individualChampion: { athleteId: 101, name: 'Alice', imageUrl: null, teamKey: '101' },
      },
    ],
    colorTeams: [],
    typeTeams: [],
    colorIndividualMedals: [],
    colorCombinedMedals: [],
  };
}

function partialListing(): Record<string, unknown> {
  return {
    saveId: SAVE,
    editions: [
      {
        cup: 'Chaos',
        sourceSeasonNumber: SEASON,
        state: 'InProgress',
        teamCount: 1024,
        podium: [],
        individualChampion: null,
      },
    ],
    colorTeams: [],
    typeTeams: [],
    colorIndividualMedals: [],
    colorCombinedMedals: [],
  };
}

function survivalPayload(): Record<string, unknown> {
  const byColor = ['White', 'Blue'].map((label, index) => ({
    key: label.toLowerCase(),
    label,
    initial: 128,
    advanced: index === 0 ? 70 : 58,
    eliminated: index === 0 ? 58 : 70,
    pending: 0,
    finalists: 0,
  }));
  const byDivision = ['Pool', 'Superleague'].map((label) => ({
    key: label,
    label,
    initial: 512,
    advanced: 64,
    eliminated: 64,
    pending: 384,
    finalists: 0,
  }));
  return {
    saveId: SAVE,
    sourceSeasonNumber: SEASON,
    sourceSeasonId: 9,
    rulesVersion: 3,
    chaosFormatVersion: 1,
    fieldSize: 1024,
    isComplete: false,
    stages: [0, 1, 2, 3, 4, 5].map((stageIndex) => ({
      stageIndex,
      groupCount: [32, 16, 8, 4, 2, 1][stageIndex],
      entrantCount: [1024, 512, 256, 128, 64, 32][stageIndex],
      isFinal: stageIndex === 5,
      isDrawn: stageIndex === 0,
      isComplete: false,
      isProvisional: stageIndex === 0,
      initialCount: stageIndex === 0 ? 1024 : 0,
      advancedCount: stageIndex === 0 ? 128 : 0,
      eliminatedCount: stageIndex === 0 ? 128 : 0,
      pendingCount: stageIndex === 0 ? 768 : 0,
      finalistCount: 0,
      byColor: stageIndex === 0 ? byColor : [],
      byDivision: stageIndex === 0 ? byDivision : [],
    })),
  };
}

function finalGroupPayload(): Record<string, unknown> {
  const standings = Array.from({ length: 32 }, (_, index) => ({
    saveAthleteId: 101 + index,
    name: index === 0 ? 'Alice' : index === 1 ? 'Bob' : index === 2 ? 'Cara' : `Athlete ${101 + index}`,
    groupRank: index + 1,
    groupScoreThousandths: 41000 - index * 100,
    baseScoreThousandths: 39000 - index * 100,
    roundWins: 2,
    isAdvanced: false,
  }));
  return {
    saveId: SAVE,
    sourceSeasonNumber: SEASON,
    sourceSeasonId: 9,
    stageIndex: 5,
    groupNumber: 1,
    isFinal: true,
    state: 'Completed',
    roundsPlayed: 16,
    totalRounds: 16,
    isComplete: true,
    standingsProvisional: false,
    drawChecksum: 'draw-5',
    entrants: [],
    standings,
    cutoff: null,
    rounds: Array.from({ length: 16 }, (_, index) => ({ roundNumber: index + 1, checksum: `r${index + 1}` })),
  };
}

function stageZeroGroupOne(): Record<string, unknown> {
  const standings = Array.from({ length: 32 }, (_, index) => ({
    saveAthleteId: 201 + index,
    name: `GroupAthlete ${201 + index}`,
    groupRank: index + 1,
    groupScoreThousandths: 30000 - index * 50,
    baseScoreThousandths: 28000 - index * 50,
    roundWins: 1,
    isAdvanced: index < 16,
  }));
  const p16 = standings[15]!;
  const p17 = standings[16]!;
  return {
    saveId: SAVE,
    sourceSeasonNumber: SEASON,
    sourceSeasonId: 9,
    stageIndex: 0,
    groupNumber: 1,
    isFinal: false,
    state: 'Completed',
    roundsPlayed: 16,
    totalRounds: 16,
    isComplete: true,
    standingsProvisional: false,
    drawChecksum: 'draw-0',
    entrants: standings.map((row) => ({
      saveAthleteId: (row as { saveAthleteId: number }).saveAthleteId,
      name: (row as { name: string }).name,
      sportingColor: 0,
      sportingColorName: 'White',
      sourceLeagueId: 3,
      sourceLeagueKind: 0,
      sourceFeederDivision: 0,
      isPoolInvite: false,
      sourceLabel: 'Superleague',
      effectiveBonusThousandths: 100,
    })),
    standings,
    cutoff: {
      p16: { saveAthleteId: (p16 as { saveAthleteId: number }).saveAthleteId, name: (p16 as { name: string }).name, groupRank: 16, groupScoreThousandths: (p16 as { groupScoreThousandths: number }).groupScoreThousandths },
      p17: { saveAthleteId: (p17 as { saveAthleteId: number }).saveAthleteId, name: (p17 as { name: string }).name, groupRank: 17, groupScoreThousandths: (p17 as { groupScoreThousandths: number }).groupScoreThousandths },
      gapThousandths: (p16 as { groupScoreThousandths: number }).groupScoreThousandths - (p17 as { groupScoreThousandths: number }).groupScoreThousandths,
      isProvisional: false,
      isOfficial: true,
    },
    rounds: Array.from({ length: 16 }, (_, index) => ({ roundNumber: index + 1, checksum: `s0g1r${index + 1}` })),
  };
}

function installGlobals(options: { edition: Record<string, unknown>; listing: Record<string, unknown> }): void {
  const g = globalThis as unknown as Record<string, unknown>;
  if (!g['localStorage']) {
    const store = new Map<string, string>();
    g['localStorage'] = {
      getItem: (k: string) => (store.has(k) ? (store.get(k) as string) : null),
      setItem: (k: string, v: string) => {
        store.set(k, v);
      },
      removeItem: (k: string) => {
        store.delete(k);
      },
      clear: () => {
        store.clear();
      },
    };
  } else {
    (g['localStorage'] as { clear: () => void }).clear();
  }
  if (!g['window']) {
    (g['window'] as unknown) = g;
  }
  const w = g['window'] as unknown as Record<string, unknown>;
  w['matchMedia'] = () => ({
    matches: false,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
  });
  w['history'] = { pushState: () => undefined, replaceState: () => undefined };
  w['dispatchEvent'] = () => true;
  w['addEventListener'] = () => undefined;
  w['removeEventListener'] = () => undefined;
  g['fetch'] = (input: unknown) => {
    const url = String(input);
    if (url.includes('/cups/chaos/survival')) {
      return Promise.resolve(okJson(survivalPayload()));
    }
    if (url.includes('/cups/chaos/stages/5/groups/1')) {
      return options.edition['isComplete'] === true
        ? Promise.resolve(okJson(finalGroupPayload()))
        : Promise.resolve(notFound());
    }
    if (url.includes('/cups/chaos/stages/0/groups/1')) {
      return Promise.resolve(okJson(stageZeroGroupOne()));
    }
    if (url.includes('/cups/chaos/stages/') && url.includes('/groups/')) {
      return Promise.resolve(notFound());
    }
    if (url.includes('/cups/chaos')) {
      return Promise.resolve(okJson(options.edition));
    }
    if (url.includes('/cups/editions')) {
      return Promise.resolve(okJson(options.listing));
    }
    return Promise.resolve(okJson({}));
  };
}

async function flush(rounds = 14): Promise<void> {
  for (let i = 0; i < rounds; i += 1) {
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
  }
}

function textOf(renderer: ReturnType<typeof create>): string {
  return JSON.stringify((renderer as unknown as { toJSON: () => unknown }).toJSON());
}

type TestInstance = {
  props: Record<string, unknown>;
  children: unknown;
  findAllByType: (t: string) => TestInstance[];
  findAll: (predicate: (node: TestInstance) => boolean) => TestInstance[];
};

function rootOf(renderer: ReturnType<typeof create>): TestInstance {
  return (renderer as unknown as { root: TestInstance }).root;
}

function clickButtonByText(renderer: ReturnType<typeof create>, text: string): boolean {
  const root = rootOf(renderer);
  const buttons = root.findAllByType('button');
  const target = buttons.find((button) => JSON.stringify(button.props['children'] ?? button.children ?? '').includes(text));
  if (!target) {
    return false;
  }
  const handler = target.props['onClick'] as unknown as (() => void) | undefined;
  if (typeof handler !== 'function') {
    return false;
  }
  act(() => {
    handler();
  });
  return true;
}

describe('MSS-112 Chaos edition page', () => {
  beforeEach(() => {
    installGlobals({ edition: completedEdition(), listing: completedListing() });
  });

  it('shows the completed strip, official champion, podium and full Final P1..P32 without a giant table', async () => {
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(React.createElement(ChaosCupEditionPage, { saveId: SAVE, season: SEASON }));
    });
    await flush();
    const text = textOf(renderer!);
    assert.ok(text.includes('1024→512→256→128→64→32'), 'stage strip renders');
    assert.ok(text.includes('Final · 1 group · 32 athletes'), 'Final format labelled');
    assert.ok(text.includes('Alice') && text.includes('Champion'), 'official champion shows only when complete');
    assert.ok(text.includes('Official podium'), 'podium shows');
    assert.ok(text.includes('Official Final — P1..P32'), 'full Final table shows');
    assert.ok(text.includes('Alice') && text.includes('Bob') && text.includes('Cara'), 'podium athletes show');
    assert.ok(text.includes('4 per sporting color'), 'first-draw rule explained');
    assert.ok(text.includes('pure random'), 'later-draw rule explained');
    const root = rootOf(renderer!);
    const rows = root.findAllByType('tr');
    assert.ok(rows.length < 60, `bounded tables only, saw ${rows.length} rows`);
    assert.ok(!text.includes('1024-row'), 'never a giant entrant table');
    const anchors = root.findAllByType('a').map((anchor) => String(anchor.props['href'] ?? ''));
    assert.ok(anchors.some((href) => href.includes(`/athletes/101`)), 'Final links to athlete profiles');
    assert.ok(
      anchors.some((href) => href === chaosLivePath(SAVE, SEASON, 5, 1)),
      'Final links to its persisted replay',
    );
  });

  it('expands one stage to link every group to replay and show matching survival analytics', async () => {
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(React.createElement(ChaosCupEditionPage, { saveId: SAVE, season: SEASON }));
    });
    await flush();
    assert.ok(clickButtonByText(renderer!, 'Show groups (32)'), 'stage 1 groups expand on demand');
    await flush(4);
    assert.ok(clickButtonByText(renderer!, 'Show color & division survival'), 'survival expands on demand');
    await flush(4);
    const text = textOf(renderer!);
    const root = rootOf(renderer!);
    const anchors = root.findAllByType('a').map((anchor) => String(anchor.props['href'] ?? ''));
    assert.ok(
      anchors.some((href) => href === chaosLivePath(SAVE, SEASON, 0, 1)),
      'stage 1 group 1 links to persisted replay',
    );
    assert.ok(
      anchors.some((href) => href === chaosLivePath(SAVE, SEASON, 0, 32)),
      'stage 1 group 32 links to persisted replay',
    );
    assert.ok(text.includes('White') && text.includes('128'), 'color survival matches the backend payload');
    assert.ok(text.includes('Pool') && text.includes('512'), 'division survival matches the backend payload');
  });

  it('expands one group to highlight the official P16/P17 cutoff with athlete links', async () => {
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(React.createElement(ChaosCupEditionPage, { saveId: SAVE, season: SEASON }));
    });
    await flush();
    assert.ok(clickButtonByText(renderer!, 'Show groups (32)'), 'groups expand first');
    await flush(4);
    assert.ok(clickButtonByText(renderer!, 'Show standings'), 'first group standings expand on demand');
    await flush(6);
    const text = textOf(renderer!);
    const root = rootOf(renderer!);
    assert.ok(text.includes('top 16 advance (official)'), 'completed group shows the official cut');
    assert.ok(text.includes('P16') && text.includes('P17'), 'P16/P17 cutoff pair shows');
    assert.ok(text.includes('cut-line-label'), 'cut line is visually prominent, not color alone');
    const anchors = root.findAllByType('a').map((anchor) => String(anchor.props['href'] ?? ''));
    assert.ok(anchors.some((href) => href.includes('/athletes/201')), 'group rows link to athlete profiles');
  });

  it('protects partial editions from a premature champion while keeping the strip accurate', async () => {
    installGlobals({ edition: partialEdition(), listing: partialListing() });
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(React.createElement(ChaosCupEditionPage, { saveId: SAVE, season: SEASON }));
    });
    await flush();
    const text = textOf(renderer!);
    assert.ok(text.includes('1024→512→256→128→64→32'), 'strip still renders while partial');
    assert.ok(text.includes('In progress'), 'partial status shows');
    assert.ok(text.includes('no official champion'), 'no premature champion');
    assert.ok(!text.includes('Official Final — P1..P32'), 'no Final table while partial');
    assert.ok(!text.includes('Official podium'), 'no podium while partial');
    assert.ok(text.includes('Not drawn'), 'undrawn stages stay locked');
    const root = rootOf(renderer!);
    const rows = root.findAllByType('tr');
    assert.ok(rows.length < 20, `partial page stays compact, saw ${rows.length} rows`);
  });
});
