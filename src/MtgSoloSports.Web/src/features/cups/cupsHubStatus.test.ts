import { describe, it, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { create, act } from 'react-test-renderer';
import { CupsHubPage } from './CupsHubPage.tsx';
import { livePath } from '../routing/routes.ts';

// MSS-070 mounted coverage: the Cups hub must surface a failed season-status
// fetch with retry (instead of swallowing it and hiding the entry point),
// and a retried success must expose Play on Live into the pending Type Cup.

(globalThis as unknown as { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
(globalThis as unknown as { React: typeof React }).React = React;

const SAVE = '33333333-3333-3333-3333-333333333333';

function okJson(data: unknown): unknown {
  return {
    ok: true,
    status: 200,
    statusText: 'OK',
    json: () => Promise.resolve(data),
    text: () => Promise.resolve(JSON.stringify(data)),
  };
}

function failJson(status: number, body: string): unknown {
  return {
    ok: false,
    status,
    statusText: 'Error',
    json: () => Promise.resolve({ error: body }),
    text: () => Promise.resolve(body),
  };
}

function pendingTypeCupStatus(): Record<string, unknown> {
  return {
    saveId: SAVE,
    currentSeasonNumber: 2,
    persistedPhase: 'CupSelectionResolved',
    computedPhase: 'CupSelectionResolved',
    sourceSeasonNumber: 2,
    nextSeasonNumber: 3,
    isInauguralTransition: false,
    globalStage: 33,
    isCurrentSeasonComplete: true,
    seasonComplete: true,
    movementResolved: true,
    qualifierResolved: true,
    rebalanced: true,
    cupSelectionResolved: true,
    cupIndividualResolved: false,
    cupTeamResolved: false,
    cupComplete: false,
    readyToStartNextSeason: false,
    expectedCup: 'TypeCup',
    legalNextActions: ['RunTypeCupTeam'],
    nextActionDetail: 'Type Cup field is allocated.',
    eventProgress: {
      event: 'type-cup-team',
      sourceSeasonNumber: 2,
      roundsPlayed: 0,
      totalRounds: 96,
      groupCount: 4,
      roundsPerGroup: 8,
      group: 1,
      roundInGroup: 1,
      tournamentPhase: 1,
      qualificationGroup: 1,
      qualificationGroupCount: 2,
      tournamentStage: 'Qualification Group 1 of 2',
    },
  };
}

function installGlobals(options: { failStatus: boolean }): { calls: string[] } {
  const calls: string[] = [];
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
    calls.push(url);
    if (url.includes('/cups/editions')) {
      return Promise.resolve(okJson({ editions: [], colorTeams: [], typeTeams: [] }));
    }
    if (url.includes('/season-status')) {
      return options.failStatus
        ? Promise.resolve(failJson(500, 'season status exploded'))
        : Promise.resolve(okJson(pendingTypeCupStatus()));
    }
    return Promise.resolve(okJson({}));
  };
  return { calls };
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

describe('MSS-070 Cups hub Type Cup entry point', () => {
  beforeEach(() => {
    installGlobals({ failStatus: true });
  });

  it('shows a recoverable error when season-status fails, hiding nothing behind emptiness', async () => {
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(React.createElement(CupsHubPage, { saveId: SAVE }));
    });
    await flush();
    const text = textOf(renderer!);
    assert.ok(text.includes('Season status unavailable'), 'failure is surfaced');
    assert.ok(text.includes('season status exploded'), 'diagnostic detail is shown');
    assert.equal(findAnchorsByText(renderer!, 'Play on Live').length, 0);
    findButtonByText(renderer!, 'Retry');
  });

  it('retry after failure exposes Play on Live into the pending Type Cup', async () => {
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(React.createElement(CupsHubPage, { saveId: SAVE }));
    });
    await flush();
    assert.ok(textOf(renderer!).includes('Season status unavailable'), 'failure shows first');

    installGlobals({ failStatus: false });
    const retry = findButtonByText(renderer!, 'Retry');
    await act(async () => {
      (retry['onClick'] as () => void)();
    });
    await flush();
    const text = textOf(renderer!);
    assert.ok(!text.includes('Season status unavailable'), 'newer success replaces the error');
    const play = findAnchorsByText(renderer!, 'Play on Live');
    assert.equal(play.length, 1);
    assert.equal(play[0]!['href'], livePath(SAVE, { event: 'type-cup-team', season: 2 }));
  });
});
