import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { create, act } from 'react-test-renderer';
import { HistoryEventView } from './HistoryEventView';

// MSS-156 regression: exhaustive-deps flagged the round-view fetch effect for
// missing `urlStage`. With a single legacy stage-less persisted round and no
// `?round=`, changing `?tournamentPhase=&qualificationGroup=` recomputed the
// exact same `selected` object, so the effect never re-ran and the replay kept
// showing the previous stage's persisted round. The fix memoizes `urlStage`
// and lists it in the effect dependencies, so a stage URL change must
// refetch the round view with the new stage.

(globalThis as unknown as { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
// tsx compiles page JSX with the classic React.createElement transform in the
// Node test runner (Vite uses the automatic runtime). Expose React globally
// so the real page modules render without a build-time transform change.
(globalThis as unknown as { React: typeof React }).React = React;

type JsonMap = Record<string, unknown>;

const SAVE = 'stage-save';

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

/** One legacy round persisted before MSS-079 stage metadata: no stage fields. */
function installRecordingFetch(): { roundViewUrls: string[] } {
  const roundViewUrls: string[] = [];
  const g = globalThis as unknown as Record<string, unknown>;
  g['fetch'] = (input: unknown) => {
    const url = String(input);
    if (url.endsWith('/rounds')) {
      return Promise.resolve(okJson({ rounds: [{ group: 1, round: 2 }] }));
    }
    if (url.includes('/rounds/2')) {
      roundViewUrls.push(url);
      return Promise.resolve(
        okJson({
          seasonNumber: 3,
          event: 'type-cup-team',
          title: 'Type Cup — Team',
          group: 1,
          roundNumber: 2,
          rulesVersion: 1,
          payloadChecksum: 'stagefetch-checksum',
          placements: [],
        } satisfies JsonMap),
      );
    }
    if (url.includes('/team-standings')) {
      return Promise.resolve(okJson({ isFinal: false, groupsCompleted: 0, teams: [], members: [] }));
    }
    if (url.includes('/cups/type/tournament')) {
      return Promise.resolve(errJson(404, 'no tournament yet'));
    }
    return Promise.resolve(errJson(404, 'not found'));
  };
  return { roundViewUrls };
}

async function flush(rounds = 10): Promise<void> {
  for (let i = 0; i < rounds; i += 1) {
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
  }
}

function propsFor(phase: number, qualificationGroup: number): React.ComponentProps<typeof HistoryEventView> {
  return {
    saveId: SAVE,
    season: 3,
    event: 'type-cup-team',
    urlGroup: null,
    urlRound: null,
    urlTournamentPhase: phase,
    urlQualificationGroup: qualificationGroup,
    onChange: () => undefined,
  };
}

describe('history event view stage URL refetch (MSS-156)', () => {
  beforeEach(() => {
    installGlobals();
  });

  afterEach(() => {
    const g = globalThis as unknown as Record<string, unknown>;
    delete g['fetch'];
  });

  it('changing the URL stage refetches the round view even when the selected round is unchanged', async () => {
    const { roundViewUrls } = installRecordingFetch();
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(React.createElement(HistoryEventView, propsFor(1, 1)));
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
    await flush();
    assert.ok(renderer);
    assert.strictEqual(roundViewUrls.length, 1, `one initial round-view fetch, got: ${roundViewUrls.join(', ')}`);
    assert.ok(
      roundViewUrls[0]!.includes('tournamentPhase=1') && roundViewUrls[0]!.includes('qualificationGroup=1'),
      `initial fetch is scoped to stage 1/1, got: ${roundViewUrls[0]}`,
    );

    // Same persisted round selected (latest fallback, unchanged identity), new
    // stage in the URL. The stale-closure bug skipped this refetch entirely.
    await act(async () => {
      (renderer as unknown as { update: (el: React.ReactElement) => void }).update(
        React.createElement(HistoryEventView, propsFor(2, 2)),
      );
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
    await flush();
    assert.ok(
      roundViewUrls.length >= 2,
      `the stage URL change must refetch the round view, got only: ${roundViewUrls.join(', ')}`,
    );
    const last = roundViewUrls[roundViewUrls.length - 1]!;
    assert.ok(
      last.includes('tournamentPhase=2') && last.includes('qualificationGroup=2'),
      `refetch is scoped to the new stage 2/2, got: ${last}`,
    );
    (renderer as unknown as { unmount: () => void }).unmount();
  });
});
