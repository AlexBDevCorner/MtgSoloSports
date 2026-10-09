import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { create, act } from 'react-test-renderer';
import { StandingsPage } from './StandingsPage';

// MSS-068 regression: Standings must keep hook order stable across
// loading -> populated / error / empty transitions. Previously tierGroups /
// tierSections useMemo calls lived after loading/error early returns, so the
// initial loading render skipped them and the post-fetch render added them,
// throwing React #310 ("Rendered more hooks than during the previous
// render") and blanking the page. These tests mount the real component with
// mocked fetch and drive it through multiple renders.
//
// contentscript.js note: browser console warnings mentioning
// `contentscript.js`, `MaxListenersExceededWarning` and `ObjectMultiplex`
// come from an injected browser/extension content script (e.g. wallet
// injection), not from application code. No `contentscript.js` exists in
// this repository. These tests do not assert on extension-owned warnings.

(globalThis as unknown as { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
// tsx compiles page JSX with the classic React.createElement transform in the
// Node test runner (Vite uses the automatic runtime). Expose React globally
// so the real page modules render without a build-time transform change.
(globalThis as unknown as { React: typeof React }).React = React;

type JsonMap = Record<string, unknown>;

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

const SAVE = 'test-save';

function seasonsInProgress(): JsonMap {
  return {
    saveId: SAVE,
    seasons: [
      {
        seasonNumber: 1,
        hasSuperleague: false,
        isComplete: false,
        competitionCount: 2,
        completedStages: 1,
        totalRounds: 16,
      },
    ],
  } as unknown as JsonMap;
}

function competitionsTwo(): JsonMap {
  return {
    saveId: SAVE,
    seasonNumber: 1,
    isSeasonComplete: false,
    competitions: [
      {
        leagueId: 1,
        name: 'Feeder Red',
        kind: 'Feeder',
        feederDivision: 1,
        leagueLevel: 'Feeder1',
        sportingColor: 0,
        sportingColorName: 'Red',
        stageCount: 32,
        completedStages: 1,
        totalRounds: 16,
        hasFinalTable: false,
      },
      {
        leagueId: 2,
        name: 'Feeder Blue',
        kind: 'Feeder',
        feederDivision: 1,
        leagueLevel: 'Feeder1',
        sportingColor: 1,
        sportingColorName: 'Blue',
        stageCount: 32,
        completedStages: 1,
        totalRounds: 16,
        hasFinalTable: false,
      },
    ],
  } as unknown as JsonMap;
}

function stagesFor(leagueId: number, leagueName: string): JsonMap {
  return {
    saveId: SAVE,
    seasonNumber: 1,
    leagueId,
    leagueName,
    stages: [
      {
        stageNumber: 1,
        completedRounds: 16,
        roundsPerStage: 16,
        isComplete: true,
        hasStandings: true,
        roundCount: 16,
      },
      {
        stageNumber: 2,
        completedRounds: 1,
        roundsPerStage: 16,
        isComplete: false,
        hasStandings: false,
        roundCount: 1,
      },
    ],
  } as unknown as JsonMap;
}

function placementsFor(leagueId: number, leagueName: string): JsonMap {
  return {
    saveId: SAVE,
    seasonNumber: 1,
    leagueId,
    leagueName,
    leagueKind: 'Feeder',
    feederDivision: 1,
    leagueLevel: 'Feeder1',
    isSeasonComplete: false,
    completedStages: 1,
    athletes: [
      {
        athleteId: 1,
        name: 'Card A',
        sportingColor: 0,
        sportingColorName: 'Red',
        imageUrl: null,
        currentEffectiveBonusThousandths: 1000,
      },
      {
        athleteId: 2,
        name: 'Card B',
        sportingColor: 0,
        sportingColorName: 'Red',
        imageUrl: null,
        currentEffectiveBonusThousandths: 2000,
      },
    ],
    placements: [
      {
        athleteId: 1,
        stageNumber: 1,
        stageRank: 1,
        earnedBonusThousandths: 1000,
        championshipPointsThousandths: 32000,
        stageScoreThousandths: 100000,
        roundWins: 5,
      },
      {
        athleteId: 2,
        stageNumber: 1,
        stageRank: 2,
        earnedBonusThousandths: 2000,
        championshipPointsThousandths: 31000,
        stageScoreThousandths: 99000,
        roundWins: 3,
      },
    ],
  } as unknown as JsonMap;
}

function currentFor(leagueId: number, leagueName: string): JsonMap {
  return {
    saveId: SAVE,
    seasonNumber: 1,
    leagueId,
    leagueName,
    completedStages: 1,
    globalStage: 2,
    isSeasonComplete: false,
    isFinal: false,
    seasonChecksum: 'abc123',
    standings: [
      {
        athleteId: 1,
        name: 'Card A',
        seasonRank: 1,
        totalChampionshipPointsThousandths: 32000,
        totalStageScoreThousandths: 100000,
        totalBaseScoreThousandths: 99000,
        stageWins: 1,
        roundWins: 5,
        isChampion: false,
        sportingColor: 0,
        sportingColorName: 'Red',
        imageUrl: null,
      },
      {
        athleteId: 2,
        name: 'Card B',
        seasonRank: 2,
        totalChampionshipPointsThousandths: 31000,
        totalStageScoreThousandths: 99000,
        totalBaseScoreThousandths: 97000,
        stageWins: 0,
        roundWins: 3,
        isChampion: false,
        sportingColor: 0,
        sportingColorName: 'Red',
        imageUrl: null,
      },
    ],
  } as unknown as JsonMap;
}

type FetchImpl = (url: string) => unknown;

function installFetch(impl: FetchImpl): void {
  const g = globalThis as unknown as Record<string, unknown>;
  g['fetch'] = (input: unknown) => {
    const url = String(input);
    if (url.includes('/superleague/inaugural') || url.includes('/superleague/automatic-movement')) {
      return Promise.resolve(errJson(404, 'not found'));
    }
    if (url.includes('/feeder-movements') || url.includes('/superleague/rebalance')) {
      return Promise.resolve(errJson(404, 'not found'));
    }
    if (url.includes('/qualifiers') || url.includes('/cups/')) {
      return Promise.resolve(errJson(404, 'not found'));
    }
    try {
      const data = impl(url) as JsonMap | null;
      if (data === null) {
        return Promise.resolve(errJson(404, 'not found'));
      }
      return Promise.resolve(okJson(data));
    } catch (failure) {
      return Promise.reject(failure);
    }
  };
}

function textOf(renderer: { toJSON: () => unknown }): string {
  return JSON.stringify(renderer.toJSON());
}

async function flush(rounds = 8): Promise<void> {
  for (let i = 0; i < rounds; i += 1) {
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
  }
}

function baseProps(): {
  saveId: string;
  urlLeagueId: number | null;
  urlSeason: number | null;
  urlView: 'season' | 'matrix' | null;
  onStandingsChange: () => void;
} {
  return {
    saveId: SAVE,
    urlLeagueId: null,
    urlSeason: null,
    urlView: null,
    onStandingsChange: () => undefined,
  };
}

describe('standings hook order (MSS-068)', () => {
  beforeEach(() => {
    installGlobals();
  });

  afterEach(() => {
    const g = globalThis as unknown as Record<string, unknown>;
    delete g['fetch'];
  });

  it('transitions loading -> populated for a new universe without hook errors or blank page', async () => {
    // Gate the seasons request so the loading render is observable before
    // async completion. Old hook-order code threw React #310 on the gated
    // resolve because tier memos appeared only after loading.
    let resolveSeasons!: (response: unknown) => void;
    const seasonsGate = new Promise<unknown>((resolve) => {
      resolveSeasons = resolve;
    });
    const g = globalThis as unknown as Record<string, unknown>;
    g['fetch'] = (input: unknown) => {
      const url = String(input);
      if (url.includes('/superleague/inaugural') || url.includes('/superleague/automatic-movement')) {
        return Promise.resolve(errJson(404, 'not found'));
      }
      if (url.includes('/feeder-movements') || url.includes('/superleague/rebalance')) {
        return Promise.resolve(errJson(404, 'not found'));
      }
      if (url.includes('/qualifiers') || url.includes('/cups/')) {
        return Promise.resolve(errJson(404, 'not found'));
      }
      if (url === `/api/saves/${SAVE}/history/seasons`) {
        return seasonsGate;
      }
      if (url === `/api/saves/${SAVE}/history/seasons/1/competitions`) {
        return Promise.resolve(okJson(competitionsTwo()));
      }
      if (url === `/api/saves/${SAVE}/history/seasons/1/competitions/1/stages`) {
        return Promise.resolve(okJson(stagesFor(1, 'Feeder Red')));
      }
      if (url === `/api/saves/${SAVE}/history/seasons/1/competitions/2/stages`) {
        return Promise.resolve(okJson(stagesFor(2, 'Feeder Blue')));
      }
      if (url === `/api/saves/${SAVE}/history/seasons/1/competitions/1/placements`) {
        return Promise.resolve(okJson(placementsFor(1, 'Feeder Red')));
      }
      if (url === `/api/saves/${SAVE}/history/seasons/1/competitions/2/placements`) {
        return Promise.resolve(okJson(placementsFor(2, 'Feeder Blue')));
      }
      if (url === `/api/saves/${SAVE}/leagues/1/standings/current`) {
        return Promise.resolve(okJson(currentFor(1, 'Feeder Red')));
      }
      if (url === `/api/saves/${SAVE}/leagues/2/standings/current`) {
        return Promise.resolve(okJson(currentFor(2, 'Feeder Blue')));
      }
      return Promise.resolve(errJson(404, 'not found'));
    };

    let renderer: ReturnType<typeof create> | null = null;
    // Mount while seasons are still pending; effects run and set loading.
    await act(async () => {
      renderer = create(React.createElement(StandingsPage, baseProps()));
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
    assert.ok(renderer, 'renderer mounted');
    const initial = textOf(renderer as unknown as { toJSON: () => unknown });
    // Loading state renders a status message, never a blank viewport.
    assert.ok(initial.includes('Loading standings'), `initial shows loading, got: ${initial.slice(0, 500)}`);

    // Async completion must not change hook order (old code threw React #310 here).
    await act(async () => {
      resolveSeasons(okJson(seasonsInProgress()));
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
    await flush();
    const populated = textOf(renderer as unknown as { toJSON: () => unknown });
    assert.ok(populated.length > 50, 'populated render is not blank');
    assert.ok(!populated.includes('Rendered more hooks'), 'no hook-order error leaked into output');
    // New-universe Season 1 in-progress data renders filters and tier groups.
    assert.ok(populated.includes('Standings filters'), 'filters render after load');
    assert.ok(populated.includes('Feeder 1'), 'tier grouping renders from data');
    assert.ok(populated.includes('Season 1'), 'season selector renders Season 1');
    assert.ok(populated.includes('Card A'), 'season table renders athletes');
    (renderer as unknown as { unmount: () => void }).unmount();
  });

  it('transitions loading -> error without hook errors or blank page', async () => {
    installFetch((url) => {
      if (url === `/api/saves/${SAVE}/history/seasons`) {
        throw new Error('Request failed (500).');
      }
      return null;
    });
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(React.createElement(StandingsPage, baseProps()));
    });
    assert.ok(renderer);
    await flush();
    const output = textOf(renderer as unknown as { toJSON: () => unknown });
    assert.ok(output.length > 20, 'error render is not blank');
    assert.ok(output.includes('Standings unavailable'), `error message renders, got: ${output.slice(0, 500)}`);
    (renderer as unknown as { unmount: () => void }).unmount();
  });

  it('transitions loading -> empty without hook errors or blank page', async () => {
    installFetch((url) => {
      if (url === `/api/saves/${SAVE}/history/seasons`) {
        return { saveId: SAVE, seasons: [] };
      }
      return null;
    });
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(React.createElement(StandingsPage, baseProps()));
    });
    assert.ok(renderer);
    await flush();
    const output = textOf(renderer as unknown as { toJSON: () => unknown });
    assert.ok(output.length > 20, 'empty render is not blank');
    assert.ok(output.includes('No seasons yet'), `empty message renders, got: ${output.slice(0, 500)}`);
    (renderer as unknown as { unmount: () => void }).unmount();
  });

  it('handles route re-entry and invalid selections without hook errors', async () => {
    installFetch((url) => {
      if (url === `/api/saves/${SAVE}/history/seasons`) {
        return seasonsInProgress();
      }
      if (url === `/api/saves/${SAVE}/history/seasons/1/competitions`) {
        return competitionsTwo();
      }
      if (url.includes('/stages')) {
        return stagesFor(1, 'Feeder Red');
      }
      if (url.includes('/placements')) {
        return placementsFor(1, 'Feeder Red');
      }
      if (url.includes('/standings/current')) {
        return currentFor(1, 'Feeder Red');
      }
      return null;
    });
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(React.createElement(StandingsPage, baseProps()));
    });
    assert.ok(renderer);
    await flush();
    const r = renderer as unknown as {
      update: (el: React.ReactElement) => void;
      toJSON: () => unknown;
      unmount: () => void;
    };
    // Navigate to an invalid season: must show an error notice, never blank, no hook error.
    await act(async () => {
      r.update(React.createElement(StandingsPage, { ...baseProps(), urlSeason: 99 }));
    });
    await flush(3);
    const invalidSeason = JSON.stringify(r.toJSON());
    assert.ok(invalidSeason.length > 20, 'invalid season is not blank');
    assert.ok(invalidSeason.includes('Season not found'), `invalid season message, got: ${invalidSeason.slice(0, 500)}`);

    // Navigate to an invalid league for a valid season.
    await act(async () => {
      r.update(React.createElement(StandingsPage, { ...baseProps(), urlSeason: 1, urlLeagueId: 999 }));
    });
    await flush(5);
    const populatedOrInvalidLeague = JSON.stringify(r.toJSON());
    assert.ok(populatedOrInvalidLeague.length > 20, 'league navigation is not blank');
    assert.ok(
      populatedOrInvalidLeague.includes('League not part of this season') ||
        populatedOrInvalidLeague.includes('Standings filters'),
      `league navigation renders content, got: ${populatedOrInvalidLeague.slice(0, 500)}`,
    );

    // Re-enter the route with a matrix view (Back/Forward simulation).
    await act(async () => {
      r.update(React.createElement(StandingsPage, { ...baseProps(), urlSeason: 1, urlLeagueId: 1, urlView: 'matrix' }));
    });
    await flush(5);
    const reentry = JSON.stringify(r.toJSON());
    assert.ok(reentry.length > 50, 're-entry is not blank');
    assert.ok(reentry.includes('Standings filters'), 'filters survive re-entry');
    r.unmount();

    // Fresh remount (direct link open) must also work.
    let second: ReturnType<typeof create> | null = null;
    await act(async () => {
      second = create(
        React.createElement(StandingsPage, { ...baseProps(), urlSeason: 1, urlLeagueId: 1, urlView: 'matrix' }),
      );
    });
    await flush();
    const direct = JSON.stringify((second as unknown as { toJSON: () => unknown }).toJSON());
    assert.ok(direct.length > 50, 'direct link is not blank');
    (second as unknown as { unmount: () => void }).unmount();
  });
});
