import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { create, act } from 'react-test-renderer';
import { LiveQualifierView } from './LiveQualifierView.tsx';

// MSS-069 behavioral coverage: mount the real LiveQualifierView with mocked
// fetch and drive Next Round, +1 reveal, round-pill navigation and completed
// replay. Companion to qualifierLive.test.ts (URL identity + route round-trip);
// these tests assert rendered behavior, not source strings.

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

interface TestPlacement {
  athleteId: number;
  name: string;
  position: number;
  baseThousandths: number;
  activeBonusThousandths: number;
  finalThousandths: number;
  cumulativeBeforeThousandths: number;
  cumulativeAfterThousandths: number;
  rankBefore: number;
  rankAfter: number;
  rankMovement: number;
  imageUrl: string | null;
  setCode: string | null;
  typeLine: string;
}

function makeField(): Array<Record<string, unknown>> {
  const rows: Array<Record<string, unknown>> = [];
  for (let id = 1; id <= 8; id += 1) {
    rows.push({
      athleteId: id,
      name: `Incumbent ${id}`,
      sportingColor: 'White',
      role: 'Incumbent',
      fromLeagueId: 10,
      fromLeagueName: 'Feeder 1 · White',
      fromSeasonRank: 16 + id,
      imageUrl: null,
      setCode: null,
      typeLine: 'Creature',
    });
  }
  for (let id = 9; id <= 16; id += 1) {
    rows.push({
      athleteId: id,
      name: `Challenger ${id}`,
      sportingColor: 'White',
      role: 'Challenger',
      fromLeagueId: 20,
      fromLeagueName: 'Feeder 2 · White',
      fromSeasonRank: id,
      imageUrl: null,
      setCode: null,
      typeLine: 'Creature',
    });
  }
  return rows;
}

function makePlacements(roundNumber: number): TestPlacement[] {
  const out: TestPlacement[] = [];
  for (let position = 1; position <= 16; position += 1) {
    const athleteId = ((position + roundNumber) % 16) + 1;
    const final = 32000 - position * 1000;
    out.push({
      athleteId,
      name: athleteId <= 8 ? `Incumbent ${athleteId}` : `Challenger ${athleteId}`,
      position,
      baseThousandths: 30000 - position * 1000,
      activeBonusThousandths: 1000,
      finalThousandths: final,
      cumulativeBeforeThousandths: (roundNumber - 1) * final,
      cumulativeAfterThousandths: roundNumber * final,
      rankBefore: athleteId,
      rankAfter: position,
      rankMovement: athleteId - position,
      imageUrl: null,
      setCode: null,
      typeLine: 'Creature',
    });
  }
  return out;
}

function makeStandings(): Array<Record<string, unknown>> {
  const rows: Array<Record<string, unknown>> = [];
  for (let rank = 1; rank <= 16; rank += 1) {
    const athleteId = rank;
    rows.push({
      athleteId,
      name: athleteId <= 8 ? `Incumbent ${athleteId}` : `Challenger ${athleteId}`,
      sportingColor: 'White',
      role: athleteId <= 8 ? 'Incumbent' : 'Challenger',
      fromLeagueId: athleteId <= 8 ? 10 : 20,
      fromLeagueName: athleteId <= 8 ? 'Feeder 1 · White' : 'Feeder 2 · White',
      fromSeasonRank: athleteId <= 8 ? 16 + athleteId : athleteId,
      qualifierRank: rank,
      qualifierScoreThousandths: 16 * (32000 - rank * 1000),
      baseScoreThousandths: 16 * (30000 - rank * 1000),
      roundWins: rank === 1 ? 4 : 1,
      isQualified: rank <= 8,
    });
  }
  return rows;
}

function makeDetail(played: number, complete: boolean): Record<string, unknown> {
  const rounds: Array<Record<string, unknown>> = [];
  for (let n = 1; n <= played; n += 1) {
    rounds.push({ roundNumber: n, rulesVersion: 1, payloadChecksum: `chk${n}` });
  }
  return {
    saveId: SAVE,
    fromSeasonNumber: SEASON,
    toSeasonNumber: SEASON + 1,
    boundary: 'Feeder1Feeder2',
    boundaryId: 1,
    sportingColor: 0,
    sportingColorName: 'White',
    roundsPlayed: played,
    totalRounds: 16,
    isComplete: complete,
    checksum: complete ? 'final-checksum-16' : '',
    rounds,
    field: makeField(),
    standings: complete ? makeStandings() : [],
  };
}

function makeRoundView(roundNumber: number): Record<string, unknown> {
  return {
    seasonNumber: SEASON,
    event: 'qualifier',
    title: 'Feeder 1 ↔ Feeder 2 · White',
    group: null,
    roundNumber,
    rulesVersion: 1,
    payloadChecksum: `chk${roundNumber}`,
    rngBeforeState: roundNumber * 100,
    rngBeforeStream: 0,
    rngAfterState: roundNumber * 100 + 1,
    rngAfterStream: 0,
    placements: makePlacements(roundNumber),
  };
}

interface FetchLog {
  gets: string[];
  posts: string[];
}

function installQualifierFetch(log: FetchLog, state: { played: number; complete: boolean }): void {
  const g = globalThis as unknown as Record<string, unknown>;
  g['fetch'] = (input: unknown, init?: { method?: string }) => {
    const url = String(input);
    const method = (init?.method ?? 'GET').toUpperCase();
    if (method !== 'GET') {
      log.posts.push(`${method} ${url}`);
      if (url.endsWith('/rounds/next')) {
        state.played += 1;
        if (state.played >= 16) {
          state.complete = true;
        }
        const roundNumber = state.played;
        return Promise.resolve(
          okJson({
            round: makeRoundView(roundNumber),
            roundsPlayed: roundNumber,
            totalRounds: 16,
            isComplete: state.complete,
            boundary: 'Feeder1Feeder2',
            sportingColor: 0,
            sportingColorName: 'White',
            fromSeasonNumber: SEASON,
            toSeasonNumber: SEASON + 1,
          }),
        );
      }
      return Promise.resolve(errJson(404, 'not found'));
    }
    log.gets.push(url);
    if (/\/rounds\/\d+\?/.test(url) || /\/rounds\/\d+$/.test(url)) {
      const match = /\/rounds\/(\d+)/.exec(url);
      const roundNumber = match ? Number.parseInt(match[1]!, 10) : 1;
      if (roundNumber < 1 || roundNumber > state.played) {
        return Promise.resolve(errJson(404, 'round not played yet'));
      }
      return Promise.resolve(okJson(makeRoundView(roundNumber)));
    }
    if (url.includes('/rounds')) {
      return Promise.resolve(okJson(makeDetail(state.played, state.complete)));
    }
    return Promise.resolve(errJson(404, 'not found'));
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

function revealCountText(renderer: ReturnType<typeof create>): string {
  // RoundReveal renders the live counter as split children ["0","/","16"],
  // so the JSON never contains the contiguous "0/16" substring. Return the
  // raw JSON; callers match the split pattern.
  return renderedText(renderer);
}

function findButtonByText(renderer: ReturnType<typeof create>, text: string): { props: Record<string, unknown> } {
  const root = (renderer as unknown as { root: { findAllByType: (t: string) => Array<{ props: Record<string, unknown>; children?: unknown }> } }).root;
  const buttons = root.findAllByType('button');
  for (const button of buttons) {
    const props = button.props as Record<string, unknown>;
    const children = props['children'];
    const flat = Array.isArray(children) ? children.join('') : String(children ?? '');
    if (flat === text || String(props['aria-label'] ?? '') === text) {
      return { props };
    }
  }
  throw new Error(`button with text ${JSON.stringify(text)} not found`);
}

function findAllRoundPills(renderer: ReturnType<typeof create>): Array<{ props: Record<string, unknown> }> {
  const root = (renderer as unknown as { root: { findAllByType: (t: string) => Array<{ props: Record<string, unknown> }> } }).root;
  return root.findAllByType('button').filter(({ props }) => {
    const p = props as Record<string, unknown>;
    const child = p['children'];
    return typeof child === 'number' && Number.isInteger(child) && (child as number) >= 1 && (child as number) <= 16;
  }) as Array<{ props: Record<string, unknown> }>;
}

function baseProps(overrides?: Partial<Parameters<typeof LiveQualifierView>[0]>): Parameters<typeof LiveQualifierView>[0] {
  return {
    saveId: SAVE,
    qualifier: 'f1f2-white',
    season: SEASON,
    urlRound: null,
    canPlay: true,
    onSelectRound: () => undefined,
    onMutated: () => undefined,
    ...overrides,
  };
}

describe('MSS-069 LiveQualifierView mounted behavior', () => {
  beforeEach(() => {
    installGlobals();
  });

  afterEach(() => {
    const g = globalThis as unknown as Record<string, unknown>;
    delete g['fetch'];
  });

  it('opens a pending qualifier at Round 0 / 16 and plays one round per Next Round click', async () => {
    const log: FetchLog = { gets: [], posts: [] };
    const state = { played: 0, complete: false };
    installQualifierFetch(log, state);
    let selected: number | null = null;
    let mutated = 0;
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(
        React.createElement(LiveQualifierView, baseProps({
          onSelectRound: (round: number | null) => {
            selected = round;
          },
          onMutated: () => {
            mutated += 1;
          },
        })),
      );
    });
    assert.ok(renderer);
    await flush();
    let text = renderedText(renderer);
    assert.ok(text.includes('Round 0 / 16'), `pending shows Round 0 / 16, got: ${text.slice(0, 800)}`);
    assert.ok(!text.includes('/ 272'), 'never shows phase-wide / 272 under the event');
    assert.ok(text.includes('No rounds played yet.'), 'pending opens before running');
    assert.ok(text.includes('Next Round'), 'Next Round control renders');

    const next = findButtonByText(renderer, 'Next Round');
    await act(async () => {
      (next.props['onClick'] as () => void)();
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
    await flush();
    // Parent would route to the new round; simulate the URL update.
    await act(async () => {
      (renderer as unknown as { update: (el: React.ReactElement) => void }).update(
        React.createElement(LiveQualifierView, baseProps({
          urlRound: selected,
          onSelectRound: (round: number | null) => {
            selected = round;
          },
          onMutated: () => {
            mutated += 1;
          },
        })),
      );
    });
    await flush();

    text = renderedText(renderer);
    assert.equal(state.played, 1, 'exactly one round persisted per click');
    assert.equal(log.posts.filter((p) => p.includes('/rounds/next')).length, 1, 'one POST to rounds/next');
    assert.equal(selected, 1, 'parent selects the persisted round');
    assert.ok(mutated >= 1, 'dashboard refresh fires after mutation');
    assert.ok(text.includes('Round 1 / 16'), `counter advances to Round 1 / 16, got: ${text.slice(0, 800)}`);
    assert.ok(!text.includes('/ 272'), 'still never / 272');
    assert.ok(text.includes('Round 1 results'), 'persisted round reveal renders');
    (renderer as unknown as { unmount: () => void }).unmount();
  });

  it('steps the animated reveal with +1 and updates cumulative standings', async () => {
    const log: FetchLog = { gets: [], posts: [] };
    const state = { played: 1, complete: false };
    installQualifierFetch(log, state);
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(React.createElement(LiveQualifierView, baseProps({ urlRound: 1 })));
    });
    assert.ok(renderer);
    await flush();
    let text = renderedText(renderer);
    assert.ok(text.includes('Round 1 / 16'), `event counter is per-event, got: ${text.slice(0, 600)}`);
    assert.ok(text.includes('Round 1 results'), 'round view loaded');
    assert.ok(revealCountText(renderer).includes('"0","/","16"'), 'reveal starts paused at 0/16 (Live manual default)');

    const plusOne = findButtonByText(renderer, 'Reveal one more card');
    await act(async () => {
      (plusOne.props['onClick'] as () => void)();
    });
    await flush(3);
    text = renderedText(renderer);
    assert.ok(revealCountText(renderer).includes('"1","/","16"'), `+1 reveals one card`);
    assert.ok(
      text.includes('Cumulative stage standings as revealed'),
      'cumulative standings section follows the reveal',
    );
    assert.equal(log.posts.length, 0, 'reveal stepping never POSTs (read-only presentation)');
    (renderer as unknown as { unmount: () => void }).unmount();
  });

  it('replays a completed qualifier with cutoff, next-event link and read-only round pills', async () => {
    const log: FetchLog = { gets: [], posts: [] };
    const state = { played: 16, complete: true };
    installQualifierFetch(log, state);
    let selected: number | null = null;
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(
        React.createElement(LiveQualifierView, baseProps({
          canPlay: false,
          onSelectRound: (round: number | null) => {
            selected = round;
          },
        })),
      );
    });
    assert.ok(renderer);
    await flush();
    let text = renderedText(renderer);
    assert.ok(text.includes('Round 16 / 16'), `completed shows Round 16 / 16, got: ${text.slice(0, 800)}`);
    assert.ok(!text.includes('/ 272'), 'completed event never shows / 272');
    assert.ok(text.includes('8 incumbents vs 8 challengers'), 'incumbent/challenger origins render');
    assert.ok(text.includes('QUALIFIED') && text.includes('ELIMINATED'), 'text badges mark the top-8 cutoff');
    assert.ok(text.includes('Cutoff'), 'cutoff row renders after rank 8');
    assert.ok(text.includes('Play next qualifier on Live'), 'next legal qualifier offered without Dashboard trip');
    assert.ok(text.includes('f1f2-blue'), 'next link targets the canonical next qualifier (f1f2-blue)');
    assert.ok(text.includes('Round 16 results'), 'latest persisted round loads for replay');

    const postsBefore = log.posts.length;
    const getsBefore = log.gets.length;
    const pills = findAllRoundPills(renderer);
    assert.equal(pills.length, 16, 'all 16 persisted rounds are navigable');
    await act(async () => {
      (pills[0]!.props['onClick'] as () => void)();
    });
    assert.equal(selected, 1, 'round pill selects the persisted round without simulating');
    // Parent routes to ?round=1; the view must re-read the persisted row only.
    await act(async () => {
      (renderer as unknown as { update: (el: React.ReactElement) => void }).update(
        React.createElement(LiveQualifierView, baseProps({
          canPlay: false,
          urlRound: 1,
          onSelectRound: (round: number | null) => {
            selected = round;
          },
        })),
      );
    });
    await flush();
    text = renderedText(renderer);
    assert.ok(text.includes('Round 1 results'), 'pill navigation replays persisted round 1');
    assert.equal(log.posts.length, postsBefore, 'replay never POSTs and never consumes RNG');
    assert.ok(log.gets.length > getsBefore, 'replay re-reads the persisted round');
    (renderer as unknown as { unmount: () => void }).unmount();
  });

  it('opens a completed qualifier at a stable ?round= URL without resimulating', async () => {
    const log: FetchLog = { gets: [], posts: [] };
    const state = { played: 16, complete: true };
    installQualifierFetch(log, state);
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(
        React.createElement(LiveQualifierView, baseProps({ canPlay: false, urlRound: 5 })),
      );
    });
    assert.ok(renderer);
    await flush();
    const text = renderedText(renderer);
    assert.ok(text.includes('Round 16 / 16'), 'event progress stays per-event even on a deep link');
    assert.ok(text.includes('Round 5 results'), 'deep link replays persisted round 5');
    assert.ok(log.gets.some((u) => u.includes('/rounds/5?')), 'round 5 is re-read from persisted rows');
    assert.equal(log.posts.length, 0, 'opening history never simulates');
    (renderer as unknown as { unmount: () => void }).unmount();
  });
});
