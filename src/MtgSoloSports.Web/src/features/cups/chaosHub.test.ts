import { describe, it, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { create, act } from 'react-test-renderer';
import { CupsHubPage } from './CupsHubPage.tsx';
import { chaosLivePath, cupEditionPath } from '../routing/routes.ts';

// MSS-111 mounted coverage: Chaos editions list in the existing Cups hub with
// stable chaos routing, official-only champion/podium, a pending Live action,
// and the responsive card grid (never a massive group table). Old saves show
// no fictitious Chaos section.

(globalThis as unknown as { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
(globalThis as unknown as { React: typeof React }).React = React;

const SAVE = '44444444-4444-4444-4444-444444444444';

function okJson(data: unknown): unknown {
  return {
    ok: true,
    status: 200,
    statusText: 'OK',
    json: () => Promise.resolve(data),
    text: () => Promise.resolve(JSON.stringify(data)),
  };
}

function chaosPartialEditions(): Record<string, unknown> {
  return {
    saveId: SAVE,
    editions: [
      {
        cup: 'Chaos',
        sourceSeasonNumber: 1,
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

function chaosCompletedEditions(): Record<string, unknown> {
  return {
    saveId: SAVE,
    editions: [
      {
        cup: 'Chaos',
        sourceSeasonNumber: 1,
        state: 'Completed',
        teamCount: 1024,
        podium: [
          { teamKey: '11', teamName: 'Alice', teamRank: 1, medal: 'Gold', teamScoreThousandths: 90000 },
          { teamKey: '22', teamName: 'Bob', teamRank: 2, medal: 'Silver', teamScoreThousandths: 80000 },
          { teamKey: '33', teamName: 'Cara', teamRank: 3, medal: 'Bronze', teamScoreThousandths: 70000 },
        ],
        individualChampion: { athleteId: 11, name: 'Alice', imageUrl: null, teamKey: '11' },
      },
    ],
    colorTeams: [],
    typeTeams: [],
    colorIndividualMedals: [],
    colorCombinedMedals: [],
  };
}

function colorOnlyEditions(): Record<string, unknown> {
  return {
    saveId: SAVE,
    editions: [
      {
        cup: 'Color',
        sourceSeasonNumber: 1,
        state: 'Completed',
        teamCount: 8,
        podium: [
          { teamKey: 'white', teamName: 'White', teamRank: 1, medal: 'Gold', teamScoreThousandths: 10000 },
        ],
        individualChampion: { athleteId: 7, name: 'Champ', imageUrl: null, teamKey: 'white' },
      },
    ],
    colorTeams: [],
    typeTeams: [],
    colorIndividualMedals: [],
    colorCombinedMedals: [],
  };
}

function chaosPendingStatus(): Record<string, unknown> {
  return {
    saveId: SAVE,
    currentSeasonNumber: 1,
    persistedPhase: 'ChaosSelectionResolved',
    computedPhase: 'ChaosSelectionResolved',
    sourceSeasonNumber: 1,
    nextSeasonNumber: 2,
    isInauguralTransition: false,
    globalStage: 33,
    isCurrentSeasonComplete: true,
    seasonComplete: true,
    movementResolved: false,
    qualifierResolved: false,
    rebalanced: false,
    cupSelectionResolved: false,
    cupIndividualResolved: false,
    cupTeamResolved: false,
    cupComplete: false,
    readyToStartNextSeason: false,
    expectedCup: 'ColorCup',
    legalNextActions: ['RunChaosCup'],
    nextActionDetail: 'Chaos Cup field is selected.',
    eventProgress: null,
    chaosEligible: true,
    chaosSelectionResolved: true,
    chaosComplete: false,
  };
}

function idleStatus(): Record<string, unknown> {
  return {
    ...chaosPendingStatus(),
    legalNextActions: ['StartNextSeason'],
    nextActionDetail: 'Done.',
    chaosComplete: true,
  };
}

function installGlobals(editions: Record<string, unknown>, status: Record<string, unknown>): void {
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
    if (url.includes('/cups/editions')) {
      return Promise.resolve(okJson(editions));
    }
    if (url.includes('/season-status')) {
      return Promise.resolve(okJson(status));
    }
    return Promise.resolve(okJson({}));
  };
}

async function flush(rounds = 12): Promise<void> {
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
  findAllByType: (t: string) => TestInstance[];
  findAll: (predicate: (node: TestInstance) => boolean) => TestInstance[];
};

function rootOf(renderer: ReturnType<typeof create>): TestInstance {
  return (renderer as unknown as { root: TestInstance }).root;
}

function findAnchors(renderer: ReturnType<typeof create>): Record<string, unknown>[] {
  return rootOf(renderer)
    .findAllByType('a')
    .map((anchor) => anchor.props);
}

function hrefsTo(renderer: ReturnType<typeof create>, href: string): Record<string, unknown>[] {
  return findAnchors(renderer).filter((props) => props['href'] === href);
}

describe('MSS-111 Chaos Cups hub', () => {
  beforeEach(() => {
    installGlobals(chaosPartialEditions(), chaosPendingStatus());
  });

  it('lists a partial Chaos edition without inventing a champion, linking to the edition and Live', async () => {
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(React.createElement(CupsHubPage, { saveId: SAVE }));
    });
    await flush();
    const text = textOf(renderer!);
    assert.ok(text.includes('Chaos Cup'), 'chaos section renders');
    assert.ok(text.includes('Season 1'), 'season shows');
    assert.ok(text.includes('In progress'), 'partial status shows');
    assert.ok(text.includes('1024') && text.includes('athletes'), 'entrant count uses athletes, not teams');
    assert.ok(!text.includes('Champion'), 'no official champion while partial');
    assert.ok(!text.includes('Alice'), 'no podium names while partial');
    assert.equal(hrefsTo(renderer!, cupEditionPath(SAVE, 'chaos', 1)).length, 1, 'card links to the chaos edition');
    assert.equal(
      hrefsTo(renderer!, chaosLivePath(SAVE, 1, null, null)).length,
      1,
      'pending Chaos links to scoped Live',
    );
  });

  it('shows the official champion and podium only once the Final completes', async () => {
    installGlobals(chaosCompletedEditions(), idleStatus());
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(React.createElement(CupsHubPage, { saveId: SAVE }));
    });
    await flush();
    const text = textOf(renderer!);
    assert.ok(text.includes('Completed'), 'completed status shows');
    assert.ok(text.includes('Champion') && text.includes('Alice'), 'official champion shows');
    assert.ok(text.includes('Alice') && text.includes('Bob') && text.includes('Cara'), 'podium shows');
    assert.ok(text.includes('Gold') && text.includes('Silver') && text.includes('Bronze'), 'medals show');
    assert.equal(hrefsTo(renderer!, cupEditionPath(SAVE, 'chaos', 1)).length, 1, 'completed edition still links');
  });

  it('keeps old saves free of fictitious Chaos while retaining Color history', async () => {
    installGlobals(colorOnlyEditions(), idleStatus());
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(React.createElement(CupsHubPage, { saveId: SAVE }));
    });
    await flush();
    const text = textOf(renderer!);
    assert.ok(text.includes('Color Cup'), 'color section stays');
    assert.ok(!text.includes('Chaos Cup —'), 'no chaos section without persisted editions');
    assert.equal(hrefsTo(renderer!, cupEditionPath(SAVE, 'color', 1)).length, 1, 'color edition still links');
  });

  it('uses the responsive card grid and never a massive group table', async () => {
    installGlobals(chaosCompletedEditions(), idleStatus());
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(React.createElement(CupsHubPage, { saveId: SAVE }));
    });
    await flush();
    const root = rootOf(renderer!);
    const editionCards = root.findAll(
      (node) =>
        typeof node.props?.['className'] === 'string' &&
        (node.props['className'] as string).split(' ').includes('edition-cards'),
    );
    assert.ok(editionCards.length >= 1, 'chaos uses the existing responsive edition-cards grid');
    const tables = root.findAllByType('table');
    assert.equal(tables.length, 0, 'no massive entrant/group table for a chaos-only hub');
    const source = textOf(renderer!);
    assert.ok(!source.includes('1024-row') && !source.includes('Group 1 · 32'), 'no giant first-round table copy');
  });
});
