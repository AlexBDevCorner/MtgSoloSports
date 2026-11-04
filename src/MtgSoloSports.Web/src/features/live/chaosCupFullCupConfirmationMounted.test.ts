import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { create, act } from 'react-test-renderer';
import { ChaosCupLiveView } from './ChaosCupLiveView.tsx';

// MSS-123: the first "Finish entire Cup" activation must only open the
// confirmation. While confirmation is open the original trigger is disabled;
// only "Yes, finish entire Cup" may POST, cancelling mutates nothing, and a
// rapid double-confirm issues exactly one tournament-wide POST.

(globalThis as unknown as { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
(globalThis as unknown as { React: typeof React }).React = React;

const SAVE = '22222222-2222-2222-2222-222222222222';
const SEASON = 2;

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
  w['matchMedia'] = () => ({
    matches: false,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
  });
  if (!w['setTimeout']) {
    w['setTimeout'] = setTimeout;
  }
  if (!w['clearTimeout']) {
    w['clearTimeout'] = clearTimeout;
  }
}

function stageEntry(
  stageIndex: number,
  groupCount: number,
  overrides?: Record<string, unknown>,
): Record<string, unknown> {
  return {
    stageIndex,
    groupCount,
    entrantCount: groupCount * 32,
    isFinal: groupCount === 1,
    state: 'Active',
    isDrawn: true,
    isComplete: false,
    drawChecksum: `draw-${stageIndex}`,
    groupsCompleted: 0,
    roundsPlayed: 0,
    totalRounds: groupCount * 16,
    standingsCount: 0,
    ...overrides,
  };
}

function editionPayload(): Record<string, unknown> {
  return {
    saveId: SAVE,
    sourceSeasonNumber: SEASON,
    sourceSeasonId: 7,
    rulesVersion: 1,
    chaosFormatVersion: 1,
    fieldSize: 1024,
    editionChecksum: 'edition-checksum',
    state: 'InProgress',
    isComplete: false,
    stageCount: 6,
    totalGroups: 63,
    totalRounds: 1008,
    roundsPlayed: 0,
    groupsCompleted: 0,
    stages: [
      stageEntry(0, 32),
      stageEntry(1, 16, { isDrawn: false, state: 'NotDrawn' }),
      stageEntry(2, 8, { isDrawn: false, state: 'NotDrawn' }),
      stageEntry(3, 4, { isDrawn: false, state: 'NotDrawn' }),
      stageEntry(4, 2, { isDrawn: false, state: 'NotDrawn' }),
      stageEntry(5, 1, { isDrawn: false, state: 'NotDrawn' }),
    ],
  };
}

function groupPayload(): Record<string, unknown> {
  return {
    saveId: SAVE,
    sourceSeasonNumber: SEASON,
    sourceSeasonId: 7,
    stageIndex: 0,
    groupNumber: 1,
    isFinal: false,
    state: 'Active',
    roundsPlayed: 0,
    totalRounds: 16,
    isComplete: false,
    standingsProvisional: true,
    drawChecksum: 'draw-0',
    entrants: [],
    standings: [],
    cutoff: null,
    rounds: [],
  };
}

function statusPayload(): Record<string, unknown> {
  return {
    saveId: SAVE,
    currentSeasonNumber: SEASON,
    persistedPhase: 'SeasonComplete',
    computedPhase: 'SeasonComplete',
    sourceSeasonNumber: SEASON,
    nextSeasonNumber: SEASON + 1,
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

interface FetchLog {
  gets: string[];
  posts: string[];
}

function installChaosFetch(log: FetchLog, delayCupMs = 0): void {
  const g = globalThis as unknown as Record<string, unknown>;
  g['fetch'] = (input: unknown, init?: { method?: string; signal?: AbortSignal }) => {
    const url = String(input);
    const method = (init?.method ?? 'GET').toUpperCase();
    if (method !== 'GET') {
      log.posts.push(`${method} ${url}`);
      if (url.includes('/cups/chaos/complete')) {
        if (delayCupMs > 0) {
          return new Promise((resolve) => {
            setTimeout(() => {
              resolve(okJson({ ok: true }));
            }, delayCupMs);
          });
        }
        return Promise.resolve(okJson({ ok: true }));
      }
      return Promise.resolve(okJson({ ok: true }));
    }
    log.gets.push(url);
    if (url.includes('/season-status')) {
      return Promise.resolve(okJson(statusPayload()));
    }
    if (url.includes('/cups/chaos/stages/') && url.includes('/groups/')) {
      return Promise.resolve(okJson(groupPayload()));
    }
    if (url.includes('/cups/chaos')) {
      return Promise.resolve(okJson(editionPayload()));
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
  for (const button of findAllButtons(renderer)) {
    if (buttonText(button.props) === text) {
      return button;
    }
  }
  throw new Error(
    `button ${JSON.stringify(text)} not found; have: ${findAllButtons(renderer).map((b) => JSON.stringify(buttonText(b.props))).join(', ')}`,
  );
}

function hasButton(renderer: ReturnType<typeof create> | null, text: string): boolean {
  return findAllButtons(renderer).some((b) => buttonText(b.props) === text);
}

function isDisabled(button: TestButton): boolean {
  return button.props['disabled'] === true;
}

type LiveProps = Parameters<typeof ChaosCupLiveView>[0];

function liveProps(overrides?: Partial<LiveProps>): LiveProps {
  return {
    saveId: SAVE,
    season: SEASON,
    urlStage: null,
    urlGroup: null,
    urlRound: null,
    onSelect: () => undefined,
    onMutated: () => undefined,
    ...overrides,
  };
}

async function mountLive(log: FetchLog, overrides?: Partial<LiveProps>): Promise<ReturnType<typeof create>> {
  installChaosFetch(log);
  let mutated = 0;
  let renderer: ReturnType<typeof create> | null = null;
  await act(async () => {
    renderer = create(
      React.createElement(
        ChaosCupLiveView,
        liveProps({
          onMutated: () => {
            mutated += 1;
          },
          ...overrides,
        }),
      ),
    );
  });
  assert.ok(renderer);
  await flush();
  (renderer as unknown as { __mutated?: () => number }).__mutated = () => mutated;
  return renderer;
}

function mutatedCount(renderer: ReturnType<typeof create>): number {
  return (renderer as unknown as { __mutated: () => number }).__mutated();
}

describe('MSS-123 ChaosCupLiveView full-Cup confirmation (mounted)', () => {
  beforeEach(() => {
    installGlobals();
  });

  afterEach(() => {
    const g = globalThis as unknown as Record<string, unknown>;
    delete g['fetch'];
  });

  it('first Finish entire Cup click only opens confirmation and disables the original trigger', async () => {
    const log: FetchLog = { gets: [], posts: [] };
    const renderer = await mountLive(log);
    assert.ok(hasButton(renderer, 'Finish entire Cup'), 'full-Cup trigger renders');
    assert.ok(hasButton(renderer, 'Finish group 1'), 'scoped group control stays available');

    await act(async () => {
      (findButton(renderer, 'Finish entire Cup').props['onClick'] as () => void)();
    });
    await flush(3);

    assert.equal(
      log.posts.filter((p) => p.includes('/cups/chaos/complete')).length,
      0,
      'first activation never POSTs the tournament completion',
    );
    assert.ok(hasButton(renderer, 'Yes, finish entire Cup'), 'dedicated affirmative renders');
    assert.ok(hasButton(renderer, 'Keep stage scope'), 'cancel renders');
    assert.ok(isDisabled(findButton(renderer, 'Finish entire Cup')), 'original trigger disabled while confirming');

    // Scoped per-group/per-stage controls stay enabled while confirming (not in flight).
    assert.equal(isDisabled(findButton(renderer, 'Finish group 1')), false, 'group scope stays usable');
    (renderer as unknown as { unmount: () => void }).unmount();
  });

  it('a rapid second click on the original trigger cannot finish the tournament', async () => {
    const log: FetchLog = { gets: [], posts: [] };
    const renderer = await mountLive(log);

    await act(async () => {
      (findButton(renderer, 'Finish entire Cup').props['onClick'] as () => void)();
    });
    await flush(3);
    assert.equal(log.posts.filter((p) => p.includes('/cups/chaos/complete')).length, 0);

    // The disabled original trigger must not execute the mutation: simulate a
    // forced repeat activation through the request path while confirmation is open.
    const original = findButton(renderer, 'Finish entire Cup');
    assert.ok(isDisabled(original), 'original stays disabled');
    await act(async () => {
      // A real disabled button cannot be clicked; invoking the request handler
      // again must still only keep the confirmation open, never POST.
      (original.props['onClick'] as () => void)();
    });
    await flush(3);
    assert.equal(
      log.posts.filter((p) => p.includes('/cups/chaos/complete')).length,
      0,
      'repeated trigger while confirming never POSTs',
    );
    assert.ok(hasButton(renderer, 'Yes, finish entire Cup'), 'confirmation still owns the mutation');
    (renderer as unknown as { unmount: () => void }).unmount();
  });

  it('cancel makes no mutation and confirm posts the tournament completion exactly once', async () => {
    const log: FetchLog = { gets: [], posts: [] };
    const renderer = await mountLive(log);

    await act(async () => {
      (findButton(renderer, 'Finish entire Cup').props['onClick'] as () => void)();
    });
    await flush(3);
    await act(async () => {
      (findButton(renderer, 'Keep stage scope').props['onClick'] as () => void)();
    });
    await flush(3);
    assert.equal(
      log.posts.filter((p) => p.includes('/cups/chaos/complete')).length,
      0,
      'cancel never POSTs',
    );
    assert.ok(!hasButton(renderer, 'Yes, finish entire Cup'), 'confirmation closes on cancel');
    assert.equal(isDisabled(findButton(renderer, 'Finish entire Cup')), false, 'trigger re-enables after cancel');

    // Re-open and double-click the affirmative control while the POST is in flight.
    await act(async () => {
      (findButton(renderer, 'Finish entire Cup').props['onClick'] as () => void)();
    });
    await flush(3);
    const yes = findButton(renderer, 'Yes, finish entire Cup');
    await act(async () => {
      (yes.props['onClick'] as () => void)();
      (yes.props['onClick'] as () => void)();
    });
    await flush(8);
    const completions = log.posts.filter((p) => p.includes('/cups/chaos/complete'));
    assert.equal(completions.length, 1, 'rapid double-confirm issues one tournament-wide POST');
    assert.ok(completions[0]!.includes('POST'), 'correct mutation endpoint invoked');
    assert.equal(mutatedCount(renderer), 1, 'dashboard refresh fires once after the confirmed run');
    (renderer as unknown as { unmount: () => void }).unmount();
  });
});
