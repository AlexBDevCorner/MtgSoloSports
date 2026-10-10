import { describe, it, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { create, act } from 'react-test-renderer';
import { SeasonFlow } from './SeasonFlow.tsx';
import { livePath } from '../routing/routes.ts';
import type { SeasonProgress, SeasonStatus } from './dashboardApi.ts';

// MSS-070 mounted coverage: the Dashboard season-flow card must surface the
// pending Type Cup (Play on Live into the correct tournament), refresh after
// a successful step, and turn a failed/unavailable status fetch into an
// actionable retry — never a false "Nothing left to run for this season."

(globalThis as unknown as { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
(globalThis as unknown as { React: typeof React }).React = React;

const SAVE = '22222222-2222-2222-2222-222222222222';

function okJson(data: unknown): unknown {
  return {
    ok: true,
    status: 200,
    statusText: 'OK',
    json: () => Promise.resolve(data),
    text: () => Promise.resolve(JSON.stringify(data)),
  };
}

function installGlobals(post: (url: string) => unknown): void {
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
  g['fetch'] = (input: unknown, init?: { method?: string }) => {
    const url = String(input);
    const method = (init?.method ?? 'GET').toUpperCase();
    if (method !== 'GET') {
      return Promise.resolve(post(url));
    }
    return Promise.resolve(okJson({}));
  };
}

function completeProgress(): SeasonProgress {
  return {
    saveId: SAVE,
    seasonNumber: 2,
    globalStage: 33,
    isSeasonComplete: true,
    leagues: [
      { leagueId: 1, leagueName: 'Superleague', leagueKind: 'Superleague', currentStage: null, completedStages: 32, isLeagueComplete: true },
    ],
  };
}

function typeCupPendingStatus(): SeasonStatus {
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

async function flush(rounds = 8): Promise<void> {
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

function findAnchorByText(renderer: ReturnType<typeof create>, text: string): Record<string, unknown> {
  const anchors = rootOf(renderer).findAllByType('a');
  for (const anchor of anchors) {
    const children = anchor.props['children'];
    const flat = Array.isArray(children) ? children.join('') : String(children ?? '');
    if (flat === text) {
      return anchor.props;
    }
  }
  throw new Error(`anchor with text ${JSON.stringify(text)} not found`);
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

describe('MSS-070 season flow Type Cup progression', () => {
  beforeEach(() => {
    installGlobals(() => okJson({}));
  });

  it('offers Play on Live into the pending Type Cup, not a false completion', async () => {
    let advanced = 0;
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(
        React.createElement(SeasonFlow, {
          saveId: SAVE,
          progress: completeProgress(),
          status: typeCupPendingStatus(),
          onAdvanced: () => {
            advanced += 1;
          },
        }),
      );
    });
    const text = textOf(renderer!);
    assert.ok(!text.includes('Nothing left to run for this season'), 'no false completion');
    assert.ok(text.includes('Run Type Cup'), 'Type Cup is the next step');
    const props = findAnchorByText(renderer!, 'Play on Live');
    assert.equal(props['href'], livePath(SAVE, { event: 'type-cup-team', season: 2 }));
    assert.equal(advanced, 0);
  });

  it('turns a failed status fetch into an error with retry', async () => {
    let advanced = 0;
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(
        React.createElement(SeasonFlow, {
          saveId: SAVE,
          progress: completeProgress(),
          status: null,
          statusError: 'Request failed (500).',
          onAdvanced: () => {
            advanced += 1;
          },
        }),
      );
    });
    const text = textOf(renderer!);
    assert.ok(!text.includes('Nothing left to run for this season'), 'error is not a completion');
    assert.ok(text.includes('Season status unavailable'), 'error is surfaced');
    assert.ok(text.includes('Request failed (500).'), 'diagnostic detail is shown');
    const retry = findButtonByText(renderer!, 'Retry');
    await act(async () => {
      (retry['onClick'] as () => void)();
    });
    assert.equal(advanced, 1);
  });

  it('does not claim completion when status is unavailable without detail', async () => {
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(
        React.createElement(SeasonFlow, {
          saveId: SAVE,
          progress: completeProgress(),
          status: null,
          onAdvanced: () => undefined,
        }),
      );
    });
    const text = textOf(renderer!);
    assert.ok(!text.includes('Nothing left to run for this season'), 'completed leagues are not a completed postseason');
    assert.ok(text.includes('Season status unavailable'), 'unavailability is explicit');
    findButtonByText(renderer!, 'Refresh');
  });

  it('runs the next step and refreshes the status afterwards', async () => {
    installGlobals((url) => {
      assert.ok(url.endsWith('/advance-next-event'), `posts to the lifecycle endpoint, got ${url}`);
      return okJson({
        saveId: SAVE,
        executedAction: 'RunTypeCupTeam',
        executedDetail: 'done',
        currentSeasonNumber: 2,
        nextSeasonNumber: 3,
      });
    });
    let advanced = 0;
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(
        React.createElement(SeasonFlow, {
          saveId: SAVE,
          progress: completeProgress(),
          status: typeCupPendingStatus(),
          onAdvanced: () => {
            advanced += 1;
          },
        }),
      );
    });
    const run = findButtonByText(renderer!, 'Run all rounds');
    await act(async () => {
      (run['onClick'] as () => void)();
    });
    await flush();
    assert.equal(advanced, 1);
    assert.ok(textOf(renderer!).includes('Cup team event finished.'), 'completion is confirmed');
  });
});
