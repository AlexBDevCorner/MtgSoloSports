import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { create, act } from 'react-test-renderer';
import { HistoryPage } from './HistoryPage';

// MSS-154 regression: the shared useSaveData hook carries the out-of-order
// abort guard, so a seasons request that resolves just after a save switch
// can no longer overwrite the new save's state. History previously kept a
// hook copy without the guard: a slow response for save A landing after the
// switch to save B would setData(A)/setLoading(false), showing wrong-save
// data and killing the new spinner. These tests resolve the old promise
// after the key change on purpose — the mock fetch ignores AbortSignal, so
// only the guard can drop the stale resolution.

(globalThis as unknown as { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
// tsx compiles page JSX with the classic React.createElement transform in the
// Node test runner (Vite uses the automatic runtime). Expose React globally
// so the real page modules render without a build-time transform change.
(globalThis as unknown as { React: typeof React }).React = React;

type JsonMap = Record<string, unknown>;

const SAVE_A = 'slow-save-a';
const SAVE_B = 'fresh-save-b';

function okJson(data: unknown): unknown {
  return {
    ok: true,
    status: 200,
    statusText: 'OK',
    json: () => Promise.resolve(data),
    text: () => Promise.resolve(JSON.stringify(data)),
  };
}

function errJson(status: number, body: string): unknown {
  return {
    ok: false,
    status,
    statusText: body,
    json: () => Promise.resolve({ error: body }),
    text: () => Promise.resolve(body),
  };
}

function seasonsFor(saveId: string, seasonNumber: number): JsonMap {
  return {
    saveId,
    seasons: [
      {
        seasonNumber,
        hasSuperleague: false,
        isComplete: false,
        competitionCount: 0,
        completedStages: 0,
        totalRounds: 16,
      },
    ],
  } as unknown as JsonMap;
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
  }
  if (!g['window']) {
    (g['window'] as unknown) = g;
  }
  const w = g['window'] as unknown as Record<string, unknown>;
  if (!w['matchMedia']) {
    w['matchMedia'] = () => ({
      matches: false,
      addEventListener: () => undefined,
      removeEventListener: () => undefined,
    });
  }
  if (!w['setTimeout']) {
    w['setTimeout'] = setTimeout;
  }
  if (!w['clearTimeout']) {
    w['clearTimeout'] = clearTimeout;
  }
}

/** Both saves' seasons requests hang on gates; everything else 404s quietly. */
function installGatedSeasonsFetch(): {
  resolveA: (response: unknown) => void;
  resolveB: (response: unknown) => void;
} {
  let resolveA!: (response: unknown) => void;
  let resolveB!: (response: unknown) => void;
  const gateA = new Promise<unknown>((resolve) => {
    resolveA = resolve;
  });
  const gateB = new Promise<unknown>((resolve) => {
    resolveB = resolve;
  });
  const g = globalThis as unknown as Record<string, unknown>;
  g['fetch'] = (input: unknown) => {
    const url = String(input);
    if (url === `/api/saves/${SAVE_A}/history/seasons`) {
      return gateA;
    }
    if (url === `/api/saves/${SAVE_B}/history/seasons`) {
      return gateB;
    }
    return Promise.resolve(errJson(404, 'not found'));
  };
  return { resolveA, resolveB };
}

async function flush(rounds = 10): Promise<void> {
  for (let i = 0; i < rounds; i += 1) {
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
  }
}

function propsFor(saveId: string): {
  saveId: string;
  urlSeason: number | null;
  urlCompetition: number | null;
  urlStage: number | null;
  urlRound: number | null;
  urlEvent: null;
  urlGroup: number | null;
  onHistoryChange: () => void;
} {
  return {
    saveId,
    urlSeason: null,
    urlCompetition: null,
    urlStage: null,
    urlRound: null,
    urlEvent: null,
    urlGroup: null,
    onHistoryChange: () => undefined,
  };
}

function textOf(renderer: { toJSON: () => unknown }): string {
  return JSON.stringify(renderer.toJSON());
}

async function mount(saveId: string): Promise<{
  r: {
    update: (el: React.ReactElement) => void;
    toJSON: () => unknown;
    unmount: () => void;
  };
}> {
  let renderer: ReturnType<typeof create> | null = null;
  await act(async () => {
    renderer = create(React.createElement(HistoryPage, propsFor(saveId)));
    await new Promise((resolve) => setTimeout(resolve, 0));
  });
  assert.ok(renderer);
  return { r: renderer as unknown as { update: (el: React.ReactElement) => void; toJSON: () => unknown; unmount: () => void } };
}

describe('history stale-response guard (MSS-154)', () => {
  beforeEach(() => {
    installGlobals();
  });

  afterEach(() => {
    const g = globalThis as unknown as Record<string, unknown>;
    delete g['fetch'];
  });

  it('a slow seasons response resolving after a save switch cannot overwrite the new save', async () => {
    const { resolveA, resolveB } = installGatedSeasonsFetch();
    const { r } = await mount(SAVE_A);
    // Save A's request hangs; save B is switched in before either resolves.
    await act(async () => {
      r.update(React.createElement(HistoryPage, propsFor(SAVE_B)));
    });
    await flush(3);
    const switched = textOf(r);
    assert.ok(switched.includes('Loading history'), `new save shows its own spinner, got: ${switched.slice(0, 300)}`);

    // The stale save-A response finally resolves AFTER the key change. The
    // unguarded hook would render save A's Season 9 and kill the spinner.
    await act(async () => {
      resolveA(okJson(seasonsFor(SAVE_A, 9)));
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
    await flush(3);
    const afterStale = textOf(r);
    assert.ok(
      afterStale.includes('Loading history'),
      `stale resolve must not kill the new save's spinner, got: ${afterStale.slice(0, 300)}`,
    );
    assert.ok(!afterStale.includes('Season 9'), 'stale save-A data must never render');

    // The fresh save-B response is the only one allowed to populate state.
    await act(async () => {
      resolveB(okJson(seasonsFor(SAVE_B, 2)));
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
    await flush();
    const populated = textOf(r);
    assert.ok(populated.includes('Season 2'), `save-B seasons render, got: ${populated.slice(0, 300)}`);
    assert.ok(!populated.includes('Season 9'), 'wrong-save season never appears');
    assert.ok(populated.includes('History filters'), 'page is usable after the race');
    r.unmount();
  });

  it('a slow failed seasons response resolving after a save switch cannot blank the new save', async () => {
    const { resolveA, resolveB } = installGatedSeasonsFetch();
    const { r } = await mount(SAVE_A);
    await act(async () => {
      r.update(React.createElement(HistoryPage, propsFor(SAVE_B)));
    });
    await flush(3);

    // Save A's request fails late, after the switch. The unguarded hook
    // would surface save A's error screen while save B is still loading.
    await act(async () => {
      resolveA(errJson(500, 'save A exploded'));
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
    await flush(3);
    const afterStaleError = textOf(r);
    assert.ok(
      afterStaleError.includes('Loading history'),
      `stale failure must not blank the new save, got: ${afterStaleError.slice(0, 300)}`,
    );
    assert.ok(!afterStaleError.includes('History unavailable'), 'stale error never renders');

    await act(async () => {
      resolveB(okJson(seasonsFor(SAVE_B, 2)));
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
    await flush();
    const populated = textOf(r);
    assert.ok(populated.includes('Season 2'), `save-B seasons render, got: ${populated.slice(0, 300)}`);
    assert.ok(!populated.includes('History unavailable'), 'stale error never surfaces');
    r.unmount();
  });
});
