import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { create, act } from 'react-test-renderer';
import { HistoryPage } from './HistoryPage';

// MSS-068 regression: History must keep hook order stable across
// loading -> populated / error / empty transitions. Previously the
// competitionGroups useMemo lived after loading/error/empty early returns,
// so the initial loading render skipped it and the post-fetch render added
// it, throwing React #310 and blanking the page. These tests mount the real
// component with mocked fetch and drive it through multiple renders.
//
// contentscript.js note: see the standings regression file —
// `contentscript.js` / `MaxListenersExceededWarning` / `ObjectMultiplex`
// warnings come from an injected browser/extension content script, not from
// application code. No `contentscript.js` exists in this repository.

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

function installFetch(impl: (url: string) => unknown): void {
  const g = globalThis as unknown as Record<string, unknown>;
  g['fetch'] = (input: unknown) => {
    const url = String(input);
    // Postseason movement / cups have no persisted result in a new universe;
    // 404 is the expected backend signal and renders quiet empty states.
    if (
      url.includes('/superleague/') ||
      url.includes('/feeder-movements') ||
      url.includes('/qualifiers') ||
      url.includes('/cups/')
    ) {
      return Promise.resolve(errJson(404, 'not found'));
    }
    if (url.includes('/history/seasons/1/events')) {
      return Promise.resolve(okJson({ events: [] }));
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

function historyUrls(): (url: string) => unknown {
  return (url: string) => {
    if (url === `/api/saves/${SAVE}/history/seasons`) {
      return seasonsInProgress();
    }
    if (url === `/api/saves/${SAVE}/history/seasons/1/competitions`) {
      return competitionsTwo();
    }
    if (url === `/api/saves/${SAVE}/history/seasons/1/competitions/1/stages`) {
      return {
        saveId: SAVE,
        seasonNumber: 1,
        leagueId: 1,
        leagueName: 'Feeder Red',
        stages: [
          {
            stageNumber: 1,
            completedRounds: 16,
            roundsPerStage: 16,
            isComplete: true,
            hasStandings: true,
            roundCount: 16,
          },
        ],
      };
    }
    if (url === `/api/saves/${SAVE}/history/seasons/1/competitions/1/stages/1/rounds`) {
      return {
        saveId: SAVE,
        seasonNumber: 1,
        leagueId: 1,
        leagueName: 'Feeder Red',
        stageNumber: 1,
        completedRounds: 1,
        roundsPerStage: 16,
        isStageComplete: false,
        rounds: [{ roundNumber: 1, rulesVersion: 1, payloadChecksum: 'chk1' }],
      };
    }
    if (url === `/api/saves/${SAVE}/history/seasons/1/competitions/1/stages/1/rounds/1`) {
      return {
        saveId: SAVE,
        seasonNumber: 1,
        leagueId: 1,
        leagueName: 'Feeder Red',
        stageNumber: 1,
        roundNumber: 1,
        rulesVersion: 1,
        payloadChecksum: 'chk1',
        rngBeforeState: 0,
        rngBeforeStream: 0,
        rngAfterState: 1,
        rngAfterStream: 0,
        placements: [
          {
            athleteId: 1,
            name: 'Card A',
            position: 1,
            baseThousandths: 10000,
            activeBonusThousandths: 1000,
            finalThousandths: 11000,
            cumulativeBeforeThousandths: 0,
            cumulativeAfterThousandths: 11000,
            rankBefore: 1,
            rankAfter: 1,
            rankMovement: 0,
            imageUrl: null,
            setCode: null,
            typeLine: 'Creature',
          },
        ],
      };
    }
    if (url === `/api/saves/${SAVE}/history/seasons/1/competitions/1/stages/1/standings`) {
      return {
        saveId: SAVE,
        seasonNumber: 1,
        leagueId: 1,
        leagueName: 'Feeder Red',
        stageNumber: 1,
        isStageComplete: true,
        stageChecksum: 'stage1',
        standings: [
          {
            athleteId: 1,
            name: 'Card A',
            stageRank: 1,
            stageScoreThousandths: 11000,
            baseScoreThousandths: 10000,
            championshipPointsThousandths: 32000,
            roundWins: 1,
            earnedBonusThousandths: 1000,
          },
        ],
      };
    }
    if (url === `/api/saves/${SAVE}/history/seasons/1/competitions/1/table`) {
      return {
        saveId: SAVE,
        seasonNumber: 1,
        leagueId: 1,
        leagueName: 'Feeder Red',
        isSeasonComplete: false,
        seasonChecksum: '',
        standings: [],
      };
    }
    return null;
  };
}

async function flush(rounds = 10): Promise<void> {
  for (let i = 0; i < rounds; i += 1) {
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
  }
}

function baseProps(): {
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
    saveId: SAVE,
    urlSeason: null,
    urlCompetition: null,
    urlStage: null,
    urlRound: null,
    urlEvent: null,
    urlGroup: null,
    onHistoryChange: () => undefined,
  };
}

describe('history hook order (MSS-068)', () => {
  beforeEach(() => {
    installGlobals();
  });

  afterEach(() => {
    const g = globalThis as unknown as Record<string, unknown>;
    delete g['fetch'];
  });

  it('transitions loading -> populated for a new universe without hook errors or blank page', async () => {
    // Gate seasons so the loading render is observable. Old code threw
    // React #310 when the gate resolved because competitionGroups appeared.
    let resolveSeasons!: (response: unknown) => void;
    const seasonsGate = new Promise<unknown>((resolve) => {
      resolveSeasons = resolve;
    });
    const inner = historyUrls();
    const g = globalThis as unknown as Record<string, unknown>;
    g['fetch'] = (input: unknown) => {
      const url = String(input);
      if (
        url.includes('/superleague/') ||
        url.includes('/feeder-movements') ||
        url.includes('/qualifiers') ||
        url.includes('/cups/')
      ) {
        return Promise.resolve(errJson(404, 'not found'));
      }
      if (url.includes('/history/seasons/1/events')) {
        return Promise.resolve(okJson({ events: [] }));
      }
      if (url === `/api/saves/${SAVE}/history/seasons`) {
        return seasonsGate;
      }
      try {
        const data = inner(url) as Record<string, unknown> | null;
        if (data === null) {
          return Promise.resolve(errJson(404, 'not found'));
        }
        return Promise.resolve(okJson(data));
      } catch (failure) {
        return Promise.reject(failure);
      }
    };
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(React.createElement(HistoryPage, baseProps()));
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
    assert.ok(renderer);
    const initial = JSON.stringify((renderer as unknown as { toJSON: () => unknown }).toJSON());
    assert.ok(initial.includes('Loading history'), `initial shows loading, got: ${initial.slice(0, 500)}`);

    await act(async () => {
      resolveSeasons(okJson(seasonsInProgress()));
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
    await flush();
    const populated = JSON.stringify((renderer as unknown as { toJSON: () => unknown }).toJSON());
    assert.ok(populated.length > 50, 'populated render is not blank');
    assert.ok(!populated.includes('Rendered more hooks'), 'no hook-order error');
    assert.ok(populated.includes('History filters'), 'filters render after load');
    assert.ok(populated.includes('Feeder 1'), 'tier grouping renders from data');
    assert.ok(populated.includes('Season 1'), 'season selector renders Season 1');
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
      renderer = create(React.createElement(HistoryPage, baseProps()));
    });
    assert.ok(renderer);
    await flush();
    const output = JSON.stringify((renderer as unknown as { toJSON: () => unknown }).toJSON());
    assert.ok(output.length > 20, 'error render is not blank');
    assert.ok(output.includes('History unavailable'), `error message renders, got: ${output.slice(0, 500)}`);
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
      renderer = create(React.createElement(HistoryPage, baseProps()));
    });
    assert.ok(renderer);
    await flush();
    const output = JSON.stringify((renderer as unknown as { toJSON: () => unknown }).toJSON());
    assert.ok(output.length > 20, 'empty render is not blank');
    assert.ok(output.includes('No history yet'), `empty message renders, got: ${output.slice(0, 500)}`);
    (renderer as unknown as { unmount: () => void }).unmount();
  });

  it('handles route re-entry and selection changes without hook errors', async () => {
    installFetch(historyUrls());
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(React.createElement(HistoryPage, baseProps()));
    });
    assert.ok(renderer);
    await flush();
    const r = renderer as unknown as {
      update: (el: React.ReactElement) => void;
      toJSON: () => unknown;
      unmount: () => void;
    };
    const populated = JSON.stringify(r.toJSON());
    assert.ok(populated.includes('History filters'), 'initial populated render');

    // Simulate Back/Forward: same route re-entered with explicit query.
    await act(async () => {
      r.update(
        React.createElement(HistoryPage, {
          ...baseProps(),
          urlSeason: 1,
          urlCompetition: 1,
          urlStage: 1,
          urlRound: 1,
        }),
      );
    });
    await flush(5);
    const reentry = JSON.stringify(r.toJSON());
    assert.ok(reentry.length > 50, 're-entry is not blank');
    assert.ok(reentry.includes('History filters'), 'filters survive re-entry');

    // Navigate back to the implicit latest selection.
    await act(async () => {
      r.update(React.createElement(HistoryPage, baseProps()));
    });
    await flush(5);
    const back = JSON.stringify(r.toJSON());
    assert.ok(back.length > 50, 'back navigation is not blank');
    assert.ok(back.includes('History filters'), 'filters survive back navigation');
    r.unmount();

    // Fresh remount via direct link must also work.
    let second: ReturnType<typeof create> | null = null;
    await act(async () => {
      second = create(
        React.createElement(HistoryPage, {
          ...baseProps(),
          urlSeason: 1,
          urlCompetition: 1,
          urlStage: 1,
          urlRound: 1,
        }),
      );
    });
    await flush();
    const direct = JSON.stringify((second as unknown as { toJSON: () => unknown }).toJSON());
    assert.ok(direct.length > 50, 'direct link is not blank');
    (second as unknown as { unmount: () => void }).unmount();
  });
});
