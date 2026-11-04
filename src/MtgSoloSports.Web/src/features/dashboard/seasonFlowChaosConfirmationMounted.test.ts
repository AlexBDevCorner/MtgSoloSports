import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { create, act } from 'react-test-renderer';
import { SeasonFlow } from './SeasonFlow.tsx';
import type { SeasonProgress, SeasonStatus } from './dashboardApi.ts';

// MSS-123: Dashboard must not silently fast-forward the entire Chaos Cup
// behind a generic "Run all rounds" label. Mounted behavior coverage:
// explicit label, confirmation gate, cancel leaves state unchanged, confirm
// posts exactly once (even on double-click), non-Chaos keeps one-click.

(globalThis as unknown as { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
(globalThis as unknown as { React: typeof React }).React = React;

const SAVE = '11111111-1111-1111-1111-111111111111';

function okJson(data: unknown): unknown {
  return {
    ok: true,
    status: 200,
    statusText: 'OK',
    json: () => Promise.resolve(data),
    text: () => Promise.resolve(JSON.stringify(data)),
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
  if (!w['location']) {
    w['location'] = { pathname: '/', search: '' };
  }
  if (!w['history']) {
    w['history'] = { pushState: () => undefined, replaceState: () => undefined };
  }
  if (!w['addEventListener']) {
    w['addEventListener'] = () => undefined;
    w['removeEventListener'] = () => undefined;
  }
  w['matchMedia'] = () => ({
    matches: false,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
  });
}

function chaosProgress(): SeasonProgress {
  return {
    saveId: SAVE,
    seasonNumber: 2,
    globalStage: 33,
    isSeasonComplete: true,
    leagues: [],
  };
}

function chaosStatus(): SeasonStatus {
  return {
    saveId: SAVE,
    currentSeasonNumber: 2,
    persistedPhase: 'SeasonComplete',
    computedPhase: 'SeasonComplete',
    sourceSeasonNumber: 2,
    nextSeasonNumber: 3,
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
    expectedCup: 'TypeCup',
    legalNextActions: ['RunChaosCup'],
    nextActionDetail: 'Backend detail.',
    eventProgress: null,
    qualifierProgress: null,
    chaosEligible: true,
    chaosSelectionResolved: true,
    chaosComplete: false,
  };
}

function qualifierStatus(): SeasonStatus {
  return {
    ...chaosStatus(),
    legalNextActions: ['RunQualifier'],
    chaosEligible: false,
    chaosSelectionResolved: false,
    eventProgress: {
      event: 'qualifier',
      sourceSeasonNumber: 2,
      roundsPlayed: 0,
      totalRounds: 16,
      groupCount: 1,
      roundsPerGroup: 16,
      group: null,
      roundInGroup: null,
      completedEvents: 0,
      totalEvents: 1,
    },
  };
}

function advanceResult(executedAction: string): Record<string, unknown> {
  return {
    saveId: SAVE,
    executedAction,
    executedDetail: `${executedAction} finished.`,
    currentSeasonNumber: 2,
    computedPhase: 'SeasonComplete',
    persistedPhase: 'SeasonComplete',
    sourceSeasonNumber: 2,
    nextSeasonNumber: 3,
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
    cupComplete: true,
    readyToStartNextSeason: false,
    expectedCup: 'TypeCup',
    legalNextActions: ['ResolveAutomaticMovement'],
    nextActionDetail: 'Backend detail.',
    eventProgress: null,
    qualifierProgress: null,
    chaosEligible: true,
    chaosSelectionResolved: true,
    chaosComplete: true,
  };
}

async function flush(rounds = 10): Promise<void> {
  for (let i = 0; i < rounds; i += 1) {
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
  }
}

function renderedText(renderer: ReturnType<typeof create> | null): string {
  assert.ok(renderer);
  return JSON.stringify((renderer as unknown as { toJSON: () => unknown }).toJSON());
}

interface TestButton {
  props: Record<string, unknown>;
}

function findAllButtons(renderer: ReturnType<typeof create> | null): TestButton[] {
  assert.ok(renderer);
  const root = renderer.root as unknown as { findAllByType: (t: string) => TestButton[] };
  return root.findAllByType('button');
}

function buttonText(props: Record<string, unknown>): string {
  const children = props['children'];
  if (Array.isArray(children)) {
    return children.map((c) => String(c ?? '')).join('');
  }
  return String(children ?? '');
}

function findButton(renderer: ReturnType<typeof create> | null, text: string): TestButton {
  const buttons = findAllButtons(renderer);
  for (const button of buttons) {
    if (buttonText(button.props) === text) {
      return button;
    }
  }
  throw new Error(
    `button ${JSON.stringify(text)} not found; have: ${buttons.map((b) => JSON.stringify(buttonText(b.props))).join(', ')}`,
  );
}

function hasButton(renderer: ReturnType<typeof create> | null, text: string): boolean {
  return findAllButtons(renderer).some((b) => buttonText(b.props) === text);
}

type FlowProps = Parameters<typeof SeasonFlow>[0];

function flowProps(overrides: Partial<FlowProps>): FlowProps {
  return {
    saveId: SAVE,
    progress: chaosProgress(),
    status: chaosStatus(),
    statusError: null,
    refreshError: null,
    onAdvanced: () => undefined,
    ...overrides,
  };
}

describe('MSS-123 SeasonFlow Chaos confirmation (mounted)', () => {
  beforeEach(() => {
    installGlobals();
  });

  afterEach(() => {
    const g = globalThis as unknown as Record<string, unknown>;
    delete g['fetch'];
  });

  it('labels the Chaos fast-forward explicitly and requires confirmation before POST', async () => {
    const posts: string[] = [];
    const g = globalThis as unknown as Record<string, unknown>;
    g['fetch'] = (input: unknown, init?: { method?: string }) => {
      const url = String(input);
      const method = (init?.method ?? 'GET').toUpperCase();
      if (method === 'POST' && url.includes('/advance-next-event')) {
        posts.push(url);
        return Promise.resolve(okJson(advanceResult('RunChaosCup')));
      }
      return Promise.resolve(okJson({}));
    };

    let advanced = 0;
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(
        React.createElement(
          SeasonFlow,
          flowProps({
            onAdvanced: () => {
              advanced += 1;
            },
          }),
        ),
      );
    });
    assert.ok(renderer);
    await flush(2);

    const text = renderedText(renderer);
    assert.ok(text.includes('Finish entire Chaos Cup'), 'explicit Chaos label');
    assert.ok(!text.includes('Run all rounds'), 'no ambiguous generic label for Chaos');

    // First activation only opens confirmation; no mutation.
    const request = findButton(renderer, 'Finish entire Chaos Cup');
    await act(async () => {
      (request.props['onClick'] as () => void)();
    });
    await flush(2);
    assert.equal(posts.length, 0, 'request alone never POSTs');
    const confirmed = renderedText(renderer);
    assert.ok(confirmed.includes('Finish the entire Chaos Cup?'), 'confirmation opens');
    assert.ok(confirmed.includes('Yes, finish entire Chaos Cup'), 'affirmative control renders');
    assert.ok(!hasButton(renderer, 'Finish entire Chaos Cup'), 'original trigger hidden while confirming');
    assert.equal(advanced, 0);
    (renderer as unknown as { unmount: () => void }).unmount();
  });

  it('cancel leaves the tournament unchanged and a rapid double-confirm posts once', async () => {
    const posts: string[] = [];
    const g = globalThis as unknown as Record<string, unknown>;
    g['fetch'] = (input: unknown, init?: { method?: string }) => {
      const url = String(input);
      const method = (init?.method ?? 'GET').toUpperCase();
      if (method === 'POST' && url.includes('/advance-next-event')) {
        posts.push(url);
        return Promise.resolve(okJson(advanceResult('RunChaosCup')));
      }
      return Promise.resolve(okJson({}));
    };

    let advanced = 0;
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(
        React.createElement(
          SeasonFlow,
          flowProps({
            onAdvanced: () => {
              advanced += 1;
            },
          }),
        ),
      );
    });
    assert.ok(renderer);
    await flush(2);

    // Open confirmation, then cancel.
    await act(async () => {
      (findButton(renderer, 'Finish entire Chaos Cup').props['onClick'] as () => void)();
    });
    await flush(2);
    assert.equal(posts.length, 0);
    await act(async () => {
      (findButton(renderer, 'Keep group/stage scope').props['onClick'] as () => void)();
    });
    await flush(2);
    assert.equal(posts.length, 0, 'cancel never POSTs');
    assert.equal(advanced, 0);
    assert.ok(hasButton(renderer, 'Finish entire Chaos Cup'), 'request returns after cancel');
    assert.ok(!renderedText(renderer).includes('Yes, finish entire Chaos Cup'), 'confirmation closes');

    // Re-open and double-click the affirmative control before the POST resolves.
    await act(async () => {
      (findButton(renderer, 'Finish entire Chaos Cup').props['onClick'] as () => void)();
    });
    await flush(2);
    const yes = findButton(renderer, 'Yes, finish entire Chaos Cup');
    await act(async () => {
      (yes.props['onClick'] as () => void)();
      (yes.props['onClick'] as () => void)();
    });
    await flush(6);
    assert.equal(posts.length, 1, 'rapid double-confirm issues one POST');
    assert.equal(posts.filter((u) => u.includes('/advance-next-event')).length, 1);
    assert.equal(advanced, 1, 'dashboard refreshes once after the confirmed run');
    (renderer as unknown as { unmount: () => void }).unmount();
  });

  it('keeps one-click Run all rounds for unrelated events', async () => {
    const posts: string[] = [];
    const g = globalThis as unknown as Record<string, unknown>;
    g['fetch'] = (input: unknown, init?: { method?: string }) => {
      const url = String(input);
      const method = (init?.method ?? 'GET').toUpperCase();
      if (method === 'POST' && url.includes('/advance-next-event')) {
        posts.push(url);
        return Promise.resolve(okJson(advanceResult('RunQualifier')));
      }
      return Promise.resolve(okJson({}));
    };

    let advanced = 0;
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(
        React.createElement(
          SeasonFlow,
          flowProps({
            status: qualifierStatus(),
            onAdvanced: () => {
              advanced += 1;
            },
          }),
        ),
      );
    });
    assert.ok(renderer);
    await flush(2);
    const text = renderedText(renderer);
    assert.ok(text.includes('Run all rounds'), 'unrelated events keep Run all rounds');
    assert.ok(!text.includes('Finish entire Chaos Cup'), 'no Chaos confirmation on other events');
    await act(async () => {
      (findButton(renderer, 'Run all rounds').props['onClick'] as () => void)();
    });
    await flush(6);
    assert.equal(posts.length, 1, 'one click still fast-forwards non-Chaos events');
    assert.equal(advanced, 1);
    (renderer as unknown as { unmount: () => void }).unmount();
  });
});
