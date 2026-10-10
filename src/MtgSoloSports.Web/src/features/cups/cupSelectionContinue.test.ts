import { describe, it, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { create, act } from 'react-test-renderer';
import { CupSelectionView } from './CupSelectionView.tsx';
import { livePath } from '../routing/routes.ts';

// MSS-070 mounted coverage: once the Type Cup squads are fully revealed, the
// selection view must offer a clear continuation into the pending team event
// (not a Dashboard-only fallback), surviving replay/reveal interactions.

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

function candidate(team: string, rank: number): Record<string, unknown> {
  return {
    athleteId: rank + (team === 'B' ? 100 : 0),
    name: `${team} Athlete ${rank}`,
    imageUrl: null,
    rank,
    selected: true,
    selectionRank: rank,
    finalRatingThousandths: 4000 - rank * 100,
    bonusNormThousandths: 800,
    performanceNormThousandths: 700,
    formNormThousandths: 600,
    prestigeNormThousandths: 500,
    bonusRawThousandths: 1000,
    performanceRawThousandths: 2000,
    formRaw: 3,
    prestigeRaw: 4,
  };
}

function report(): Record<string, unknown> {
  return {
    saveId: SAVE,
    sourceSeasonNumber: 2,
    rulesVersion: 3,
    hasFullRanking: true,
    teamSize: 4,
    bonusWeightPermille: 350,
    performanceWeightPermille: 350,
    formWeightPermille: 150,
    prestigeWeightPermille: 150,
    teams: [
      { teamKey: 'A', teamName: 'A Team', candidateCount: 4, ranking: [1, 2, 3, 4].map((rank) => candidate('A', rank)) },
      { teamKey: 'B', teamName: 'B Team', candidateCount: 4, ranking: [1, 2, 3, 4].map((rank) => candidate('B', rank)) },
    ],
  };
}

function installGlobals(): void {
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
    if (url.includes('/cups/type/selection-report')) {
      return Promise.resolve(okJson(report()));
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
};

function rootOf(renderer: ReturnType<typeof create>): TestInstance {
  return (renderer as unknown as { root: TestInstance }).root;
}

function findAnchorsByText(renderer: ReturnType<typeof create>, text: string): Record<string, unknown>[] {
  return rootOf(renderer)
    .findAllByType('a')
    .map((anchor) => anchor.props)
    .filter((props) => {
      const children = props['children'];
      const flat = Array.isArray(children) ? children.join('') : String(children ?? '');
      return flat === text;
    });
}

function findButtonByText(renderer: ReturnType<typeof create>, text: string): Record<string, unknown> {
  const buttons = rootOf(renderer).findAllByType('button');
  for (const button of buttons) {
    const children = button.props['children'];
    const flat = Array.isArray(children) ? children.join('') : String(children ?? '');
    if (flat === text) {
      return button.props;
    }
  }
  throw new Error(`button with text ${JSON.stringify(text)} not found`);
}

describe('MSS-070 selection reveal continuation', () => {
  beforeEach(() => {
    installGlobals();
  });

  it('offers Play into the pending Type Cup once squads are revealed', async () => {
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(
        React.createElement(CupSelectionView, {
          saveId: SAVE,
          selection: 'type-cup-selection',
          season: 2,
          canAnnounce: false,
          nextEvent: 'type-cup-team',
          onPin: () => undefined,
          onMutated: () => undefined,
        }),
      );
    });
    await flush();
    assert.ok(textOf(renderer!).includes('Squads announced'), 'saved selection opens revealed');
    const play = findAnchorsByText(renderer!, 'Play Type Cup — team');
    assert.equal(play.length, 1);
    assert.equal(play[0]!['href'], livePath(SAVE, { event: 'type-cup-team', season: 2 }));
  });

  it('keeps the continuation after replaying the reveal', async () => {
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(
        React.createElement(CupSelectionView, {
          saveId: SAVE,
          selection: 'type-cup-selection',
          season: 2,
          canAnnounce: false,
          nextEvent: 'type-cup-team',
          onPin: () => undefined,
          onMutated: () => undefined,
        }),
      );
    });
    await flush();
    const replay = findButtonByText(renderer!, 'Replay the reveal');
    await act(async () => {
      (replay['onClick'] as () => void)();
    });
    await flush();
    assert.equal(findAnchorsByText(renderer!, 'Play Type Cup — team').length, 0);
    const revealAll = findButtonByText(renderer!, 'Reveal all');
    await act(async () => {
      (revealAll['onClick'] as () => void)();
    });
    await flush();
    const play = findAnchorsByText(renderer!, 'Play Type Cup — team');
    assert.equal(play.length, 1);
    assert.equal(play[0]!['href'], livePath(SAVE, { event: 'type-cup-team', season: 2 }));
  });
});
