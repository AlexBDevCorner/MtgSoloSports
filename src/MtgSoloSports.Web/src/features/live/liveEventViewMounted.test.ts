import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { create, act } from 'react-test-renderer';
import { LiveEventView } from './LiveEventView.tsx';

// MSS-069 correction coverage: mount the real Superleague LiveEventView when
// the qualifier event is complete and verify the 17-event Live experience
// does not dead-end: next canonical qualifier on Live plus fast-forward.

(globalThis as unknown as { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
(globalThis as unknown as { React: typeof React }).React = React;

const SAVE = '11111111-1111-1111-1111-111111111111';
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
}

function makePlacements(count: number): Array<Record<string, unknown>> {
  const out: Array<Record<string, unknown>> = [];
  for (let position = 1; position <= count; position += 1) {
    const final = 32000 - position * 500;
    out.push({
      athleteId: position,
      name: `Athlete ${position}`,
      position,
      baseThousandths: 30000 - position * 500,
      activeBonusThousandths: 1000,
      finalThousandths: final,
      cumulativeBeforeThousandths: 15 * final,
      cumulativeAfterThousandths: 16 * final,
      rankBefore: position,
      rankAfter: position,
      rankMovement: 0,
      imageUrl: null,
      setCode: null,
      typeLine: 'Creature',
    });
  }
  return out;
}

function installSuperleagueFetch(log: { gets: string[]; posts: string[] }, options?: { tiered?: boolean }): void {
  const tiered = options?.tiered ?? true;
  const g = globalThis as unknown as Record<string, unknown>;
  g['fetch'] = (input: unknown, init?: { method?: string }) => {
    const url = String(input);
    const method = (init?.method ?? 'GET').toUpperCase();
    if (method !== 'GET') {
      log.posts.push(`${method} ${url}`);
      if (url.endsWith('/qualifiers/run-all')) {
        return Promise.resolve(
          okJson({
            saveId: SAVE,
            fromSeasonNumber: SEASON,
            toSeasonNumber: SEASON + 1,
            totalStandings: 272,
            totalRounds: 272,
            alreadyCompleted: ['Superleague'],
            executedNow: ['F1F2:0'],
          }),
        );
      }
      return Promise.resolve(okJson({}));
    }
    log.gets.push(url);
    if (url.includes(`/seasons/${SEASON}/progress`)) {
      const tieredLeagues = [
        { leagueId: 1, leagueName: 'Superleague', leagueKind: 'Superleague', feederDivision: 0, leagueLevel: 'Superleague', currentStage: null, completedStages: 32, isLeagueComplete: true },
        { leagueId: 2, leagueName: 'White F1', leagueKind: 'Feeder', feederDivision: 1, leagueLevel: 'Feeder1', currentStage: null, completedStages: 32, isLeagueComplete: true },
        { leagueId: 3, leagueName: 'White F2', leagueKind: 'Feeder', feederDivision: 2, leagueLevel: 'Feeder2', currentStage: null, completedStages: 32, isLeagueComplete: true },
        { leagueId: 4, leagueName: 'White F3', leagueKind: 'Feeder', feederDivision: 3, leagueLevel: 'Feeder3', currentStage: null, completedStages: 32, isLeagueComplete: true },
      ];
      const v1Leagues = [
        { leagueId: 1, leagueName: 'Superleague', leagueKind: 'Superleague', feederDivision: 0, leagueLevel: 'Superleague', currentStage: null, completedStages: 32, isLeagueComplete: true },
        { leagueId: 2, leagueName: 'White League', leagueKind: 'Feeder', feederDivision: 0, leagueLevel: 'Feeder1', currentStage: null, completedStages: 32, isLeagueComplete: true },
      ];
      return Promise.resolve(
        okJson({
          saveId: SAVE,
          seasonNumber: SEASON,
          globalStage: 32,
          isSeasonComplete: true,
          leagues: tiered ? tieredLeagues : v1Leagues,
        }),
      );
    }
    if (url.includes(`/history/seasons/${SEASON}/events/qualifier/rounds/`)) {
      const match = /\/rounds\/(\d+)/.exec(url);
      const roundNumber = match ? Number.parseInt(match[1]!, 10) : 16;
      return Promise.resolve(
        okJson({
          seasonNumber: SEASON,
          event: 'qualifier',
          title: 'Superleague qualifier',
          group: null,
          roundNumber,
          rulesVersion: 1,
          payloadChecksum: `sl-chk${roundNumber}-persisted`,
          placements: makePlacements(32),
        }),
      );
    }
    if (url.includes(`/history/seasons/${SEASON}/events/qualifier/rounds`)) {
      return Promise.resolve(
        okJson({
          rounds: Array.from({ length: 16 }, (_, i) => ({ group: null, round: i + 1 })),
        }),
      );
    }
    if (url.includes(`/history/seasons/${SEASON}/events`)) {
      return Promise.resolve(
        okJson({
          events: [
            {
              event: 'qualifier',
              title: 'Superleague qualifier',
              roundsPlayed: 16,
              totalRounds: 16,
              groupCount: 1,
              roundsPerGroup: 16,
              isComplete: true,
            },
          ],
        }),
      );
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

function renderedText(renderer: ReturnType<typeof create>): string {
  return JSON.stringify((renderer as unknown as { toJSON: () => unknown }).toJSON());
}

function findButtonByText(renderer: ReturnType<typeof create>, text: string): { props: Record<string, unknown> } {
  const root = (renderer as unknown as { root: { findAllByType: (t: string) => Array<{ props: Record<string, unknown> }> } }).root;
  const buttons = root.findAllByType('button');
  for (const button of buttons) {
    const props = button.props as Record<string, unknown>;
    const children = props['children'];
    const flat = Array.isArray(children) ? children.join('') : String(children ?? '');
    if (flat === text) {
      return { props };
    }
  }
  throw new Error(`button with text ${JSON.stringify(text)} not found`);
}

describe('MSS-069 Superleague Live completion navigates to the 17-event flow', () => {
  beforeEach(() => {
    installGlobals();
  });

  afterEach(() => {
    const g = globalThis as unknown as Record<string, unknown>;
    delete g['fetch'];
  });

  it('offers the next qualifier on Live and run-all fast-forward after round 16', async () => {
    const log = { gets: [] as string[], posts: [] as string[] };
    installSuperleagueFetch(log);
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(
        React.createElement(LiveEventView, {
          saveId: SAVE,
          event: 'qualifier',
          season: SEASON,
          progress: null,
          urlGroup: null,
          urlRound: null,
          onSelectRound: () => undefined,
          onMutated: () => undefined,
        }),
      );
    });
    assert.ok(renderer);
    await flush();

    const text = renderedText(renderer);
    assert.ok(text.includes('Superleague qualifier complete'), 'completion notice renders');
    assert.ok(text.includes('Play next qualifier on Live'), 'next qualifier offered without Dashboard trip');
    assert.ok(text.includes('f1f2-white'), 'next link targets canonical F1F2 White qualifier');
    assert.ok(text.includes('All 17 qualifiers'), 'overview link renders');
    assert.ok(text.includes('Run all remaining qualifiers'), 'Live fast-forward renders');
    assert.ok(!text.includes('/ 272'), 'phase-wide total never appears under the event title');

    const runAll = findButtonByText(renderer, 'Run all remaining qualifiers');
    await act(async () => {
      (runAll.props['onClick'] as () => void)();
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
    await flush(4);
    assert.ok(
      log.posts.some((p) => p.includes('/qualifiers/run-all')),
      `fast-forward POSTs run-all, got: ${JSON.stringify(log.posts)}`,
    );
    (renderer as unknown as { unmount: () => void }).unmount();
  });

  it('keeps the legacy single-qualifier completion for v1 saves (no phantom F1F2 link)', async () => {
    const log = { gets: [] as string[], posts: [] as string[] };
    installSuperleagueFetch(log, { tiered: false });
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(
        React.createElement(LiveEventView, {
          saveId: SAVE,
          event: 'qualifier',
          season: SEASON,
          progress: null,
          urlGroup: null,
          urlRound: null,
          onSelectRound: () => undefined,
          onMutated: () => undefined,
        }),
      );
    });
    assert.ok(renderer);
    await flush();

    const text = renderedText(renderer);
    assert.ok(text.includes('Superleague qualifier complete'), 'completion notice renders');
    assert.ok(text.includes('Continue on the Dashboard'), 'v1 keeps the legacy Dashboard CTA');
    assert.ok(!text.includes('Play next qualifier on Live'), 'v1 never offers a phantom feeder qualifier');
    assert.ok(!text.includes('f1f2-white'), 'v1 never links to a nonexistent F1F2 qualifier');
    assert.ok(!text.includes('All 17 qualifiers'), 'v1 never claims a 17-event phase');
    assert.ok(!text.includes('Qualifier 1 of 17'), 'v1 never labels the sole qualifier as 1 of 17');
    assert.ok(!text.includes('Run all remaining qualifiers'), 'v1 hides the 17-event fast-forward');
    assert.ok(!text.includes('/ 272'), 'phase-wide total never appears under the event title');
    (renderer as unknown as { unmount: () => void }).unmount();
  });
});
