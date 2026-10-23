import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { create, act } from 'react-test-renderer';
import { StandingsPage, defaultLeagueIdFor } from './StandingsPage';

(globalThis as unknown as { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
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
}

const SAVE = 'superleague-save';

function athleteRows(): JsonMap[] {
  return [
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
  ] as unknown as JsonMap[];
}

function placementCells(): JsonMap[] {
  return [
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
      stageNumber: 2,
      stageRank: 2,
      earnedBonusThousandths: 2000,
      championshipPointsThousandths: 31000,
      stageScoreThousandths: 99000,
      roundWins: 3,
    },
  ] as unknown as JsonMap[];
}

function tableRows(): JsonMap[] {
  return [
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
    },
  ] as unknown as JsonMap[];
}

function twoSeasons(): JsonMap {
  return {
    saveId: SAVE,
    seasons: [
      {
        seasonNumber: 1,
        hasSuperleague: true,
        isComplete: true,
        competitionCount: 2,
        completedStages: 32,
        totalRounds: 512,
      },
      {
        seasonNumber: 2,
        hasSuperleague: true,
        isComplete: true,
        competitionCount: 2,
        completedStages: 32,
        totalRounds: 512,
      },
    ],
  } as unknown as JsonMap;
}

function oneSeason(): JsonMap {
  return {
    saveId: SAVE,
    seasons: [
      {
        seasonNumber: 1,
        hasSuperleague: true,
        isComplete: true,
        competitionCount: 3,
        completedStages: 32,
        totalRounds: 512,
      },
    ],
  } as unknown as JsonMap;
}

function competitionsFor(season: number, variant: 'stable' | 'missing' | 'superHigh'): JsonMap {
  if (variant === 'superHigh') {
    return {
      saveId: SAVE,
      seasonNumber: 1,
      isSeasonComplete: true,
      competitions: [
        {
          leagueId: 2,
          name: 'Feeder Red',
          kind: 'Feeder',
          feederDivision: 1,
          leagueLevel: 'Feeder1',
          sportingColor: 0,
          sportingColorName: 'Red',
          stageCount: 32,
          completedStages: 32,
          totalRounds: 512,
          hasFinalTable: true,
        },
        {
          leagueId: 3,
          name: 'Feeder Blue',
          kind: 'Feeder',
          feederDivision: 1,
          leagueLevel: 'Feeder1',
          sportingColor: 1,
          sportingColorName: 'Blue',
          stageCount: 32,
          completedStages: 32,
          totalRounds: 512,
          hasFinalTable: true,
        },
        {
          leagueId: 9,
          name: 'Superleague',
          kind: 'Superleague',
          feederDivision: 0,
          leagueLevel: 'Superleague',
          sportingColor: 0,
          sportingColorName: '-',
          stageCount: 32,
          completedStages: 32,
          totalRounds: 512,
          hasFinalTable: true,
        },
      ],
    } as unknown as JsonMap;
  }
  if (variant === 'missing') {
    if (season === 1) {
      return {
        saveId: SAVE,
        seasonNumber: 1,
        isSeasonComplete: true,
        competitions: [
          {
            leagueId: 1,
            name: 'Superleague',
            kind: 'Superleague',
            feederDivision: 0,
            leagueLevel: 'Superleague',
            sportingColor: 0,
            sportingColorName: '-',
            stageCount: 32,
            completedStages: 32,
            totalRounds: 512,
            hasFinalTable: true,
          },
          {
            leagueId: 2,
            name: 'Feeder Red',
            kind: 'Feeder',
            feederDivision: 1,
            leagueLevel: 'Feeder1',
            sportingColor: 0,
            sportingColorName: 'Red',
            stageCount: 32,
            completedStages: 32,
            totalRounds: 512,
            hasFinalTable: true,
          },
        ],
      } as unknown as JsonMap;
    }
    return {
      saveId: SAVE,
      seasonNumber: 2,
      isSeasonComplete: true,
      competitions: [
        {
          leagueId: 1,
          name: 'Superleague',
          kind: 'Superleague',
          feederDivision: 0,
          leagueLevel: 'Superleague',
          sportingColor: 0,
          sportingColorName: '-',
          stageCount: 32,
          completedStages: 32,
          totalRounds: 512,
          hasFinalTable: true,
        },
      ],
    } as unknown as JsonMap;
  }
  // stable: same ids both seasons
  return {
    saveId: SAVE,
    seasonNumber: season,
    isSeasonComplete: true,
    competitions: [
      {
        leagueId: 1,
        name: 'Superleague',
        kind: 'Superleague',
        feederDivision: 0,
        leagueLevel: 'Superleague',
        sportingColor: 0,
        sportingColorName: '-',
        stageCount: 32,
        completedStages: 32,
        totalRounds: 512,
        hasFinalTable: true,
      },
      {
        leagueId: 2,
        name: 'Feeder Red',
        kind: 'Feeder',
        feederDivision: 1,
        leagueLevel: 'Feeder1',
        sportingColor: 0,
        sportingColorName: 'Red',
        stageCount: 32,
        completedStages: 32,
        totalRounds: 512,
        hasFinalTable: true,
      },
    ],
  } as unknown as JsonMap;
}

function stagesFor(season: number, leagueId: number, leagueName: string): JsonMap {
  return {
    saveId: SAVE,
    seasonNumber: season,
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
    ],
  } as unknown as JsonMap;
}

function placementsFor(
  season: number,
  leagueId: number,
  leagueName: string,
  kind = 'Feeder',
  level: string | null = 'Feeder1',
): JsonMap {
  return {
    saveId: SAVE,
    seasonNumber: season,
    leagueId,
    leagueName,
    leagueKind: kind,
    feederDivision: kind === 'Superleague' ? 0 : 1,
    leagueLevel: level,
    isSeasonComplete: true,
    completedStages: 32,
    athletes: athleteRows(),
    placements: placementCells(),
  } as unknown as JsonMap;
}

function tableFor(season: number, leagueId: number, leagueName: string): JsonMap {
  return {
    saveId: SAVE,
    seasonNumber: season,
    leagueId,
    leagueName,
    isSeasonComplete: true,
    seasonChecksum: `final-${season}-${leagueId}`,
    standings: tableRows(),
  } as unknown as JsonMap;
}

function leagueNameFor(leagueId: number): { name: string; kind: string; level: string | null } {
  if (leagueId === 1 || leagueId === 9) {
    return { name: 'Superleague', kind: 'Superleague', level: 'Superleague' };
  }
  return { name: 'Feeder Red', kind: 'Feeder', level: 'Feeder1' };
}

async function flush(rounds = 12): Promise<void> {
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

function installFetch(variant: 'stable' | 'missing' | 'superHigh'): void {
  const g = globalThis as unknown as Record<string, unknown>;
  g['fetch'] = (input: unknown) => {
    const url = String(input);
    if (
      url.includes('/superleague/inaugural') ||
      url.includes('/superleague/automatic-movement') ||
      url.includes('/feeder-movements') ||
      url.includes('/superleague/rebalance') ||
      url.includes('/qualifiers') ||
      url.includes('/cups/')
    ) {
      return Promise.resolve(errJson(404, 'not found'));
    }
    if (url === `/api/saves/${SAVE}/history/seasons`) {
      return Promise.resolve(okJson(variant === 'superHigh' ? oneSeason() : twoSeasons()));
    }
    const compMatch = /\/history\/seasons\/(\d+)\/competitions$/.exec(url);
    if (compMatch) {
      const season = Number.parseInt(compMatch[1]!, 10);
      return Promise.resolve(okJson(competitionsFor(season, variant)));
    }
    const stagesMatch = /\/history\/seasons\/(\d+)\/competitions\/(\d+)\/stages$/.exec(url);
    if (stagesMatch) {
      const season = Number.parseInt(stagesMatch[1]!, 10);
      const leagueId = Number.parseInt(stagesMatch[2]!, 10);
      const { name } = leagueNameFor(leagueId);
      return Promise.resolve(okJson(stagesFor(season, leagueId, name)));
    }
    const placementsMatch = /\/history\/seasons\/(\d+)\/competitions\/(\d+)\/placements$/.exec(url);
    if (placementsMatch) {
      const season = Number.parseInt(placementsMatch[1]!, 10);
      const leagueId = Number.parseInt(placementsMatch[2]!, 10);
      // Simulate missing league in destination season for the missing variant.
      if (variant === 'missing' && season === 2 && leagueId === 2) {
        return Promise.resolve(errJson(404, 'not found'));
      }
      const { name, kind, level } = leagueNameFor(leagueId);
      return Promise.resolve(okJson(placementsFor(season, leagueId, name, kind, level)));
    }
    const tableMatch = /\/history\/seasons\/(\d+)\/competitions\/(\d+)\/table$/.exec(url);
    if (tableMatch) {
      const season = Number.parseInt(tableMatch[1]!, 10);
      const leagueId = Number.parseInt(tableMatch[2]!, 10);
      if (variant === 'missing' && season === 2 && leagueId === 2) {
        return Promise.resolve(errJson(404, 'not found'));
      }
      const { name } = leagueNameFor(leagueId);
      return Promise.resolve(okJson(tableFor(season, leagueId, name)));
    }
    if (url.includes('/standings/current')) {
      return Promise.resolve(errJson(404, 'not found'));
    }
    return Promise.resolve(errJson(404, 'not found'));
  };
}

function selectsOf(renderer: { root: { findAllByType: (t: string) => Array<{ props: { value?: unknown; onChange?: (e: { target: { value: string } }) => void } }> } }): Array<{
  props: { value?: unknown; onChange?: (e: { target: { value: string } }) => void };
}> {
  return renderer.root.findAllByType('select') as unknown as Array<{
    props: { value?: unknown; onChange?: (e: { target: { value: string } }) => void };
  }>;
}

describe('MSS-084 superleague default and preserved selection', () => {
  beforeEach(() => {
    installGlobals();
  });

  afterEach(() => {
    const g = globalThis as unknown as Record<string, unknown>;
    delete g['fetch'];
  });

  it('defaults to Superleague by id, not list position', () => {
    // Superleague has the largest id here; smallest-id fallback would pick 2.
    assert.equal(
      defaultLeagueIdFor([
        { leagueId: 2, kind: 'Feeder', leagueLevel: 'Feeder1' },
        { leagueId: 3, kind: 'Feeder', leagueLevel: 'Feeder1' },
        { leagueId: 9, kind: 'Superleague', leagueLevel: 'Superleague' },
      ]),
      9,
    );
    // kind alone is enough for legacy rows without a level.
    assert.equal(
      defaultLeagueIdFor([
        { leagueId: 5, kind: 'Feeder', leagueLevel: null },
        { leagueId: 4, kind: 'Superleague', leagueLevel: null },
      ]),
      4,
    );
  });

  it('falls back to the smallest id when no Superleague exists', () => {
    assert.equal(
      defaultLeagueIdFor([
        { leagueId: 6, kind: 'Feeder', leagueLevel: 'Feeder1' },
        { leagueId: 5, kind: 'Feeder', leagueLevel: 'Feeder1' },
      ]),
      5,
    );
    assert.equal(defaultLeagueIdFor([]), null);
  });

  it('opens fresh on Superleague even when its id sorts last', async () => {
    installFetch('superHigh');
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(React.createElement(StandingsPage, baseProps()));
    });
    assert.ok(renderer);
    await flush();
    const output = JSON.stringify((renderer as unknown as { toJSON: () => unknown }).toJSON());
    assert.ok(output.includes('Superleague'), 'Superleague tier renders');
    assert.ok(output.includes('Card A'), 'Superleague rows render');
    const selects = selectsOf(renderer as unknown as Parameters<typeof selectsOf>[0]);
    // League is the first select; it must actually be Superleague id 9, not 2.
    assert.equal(selects[0]!.props.value, 9);
    (renderer as unknown as { unmount: () => void }).unmount();
  });

  it('keeps an explicitly selected feeder across seasons in both directions', async () => {
    installFetch('stable');
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(React.createElement(StandingsPage, baseProps()));
    });
    assert.ok(renderer);
    await flush();
    const r = renderer as unknown as {
      root: { findAllByType: (t: string) => Array<{ props: { value?: unknown; onChange?: (e: { target: { value: string } }) => void } }> };
      toJSON: () => unknown;
      unmount: () => void;
    };
    // Fresh open defaults to Superleague on the latest season (2).
    assert.equal(selectsOf(r)[0]!.props.value, 1);
    // Pick Feeder Red (2).
    await act(async () => {
      selectsOf(r)[0]!.props.onChange!({ target: { value: '2' } });
    });
    await flush();
    assert.equal(selectsOf(r)[0]!.props.value, 2);
    let output = JSON.stringify(r.toJSON());
    assert.ok(output.includes('Feeder Red'), 'feeder standings render after explicit pick');

    // Season 2 -> 1 keeps Feeder 2 by stable id.
    await act(async () => {
      selectsOf(r)[1]!.props.onChange!({ target: { value: '1' } });
    });
    await flush();
    assert.equal(selectsOf(r)[1]!.props.value, 1, 'season selector moved to 1');
    assert.equal(selectsOf(r)[0]!.props.value, 2, 'league stays Feeder 2 after season change');
    output = JSON.stringify(r.toJSON());
    assert.ok(output.includes('Feeder Red'), 'standings stay on Feeder Red in season 1');
    assert.ok(output.includes('Season 1'), 'headings match season 1');

    // Season 1 -> 2 keeps Feeder 2 again, not resetting to Superleague.
    await act(async () => {
      selectsOf(r)[1]!.props.onChange!({ target: { value: '2' } });
    });
    await flush();
    assert.equal(selectsOf(r)[1]!.props.value, 2, 'season selector back to 2');
    assert.equal(selectsOf(r)[0]!.props.value, 2, 'league still Feeder 2 after return');
    output = JSON.stringify(r.toJSON());
    assert.ok(output.includes('Feeder Red'), 'standings still Feeder Red in season 2');
    assert.ok(output.includes('Season 2'), 'headings match season 2');
    r.unmount();
  });

  it('falls back to Superleague when the selected division is missing, then preserves the fallback', async () => {
    installFetch('missing');
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(React.createElement(StandingsPage, baseProps()));
    });
    assert.ok(renderer);
    await flush();
    const r = renderer as unknown as {
      root: { findAllByType: (t: string) => Array<{ props: { value?: unknown; onChange?: (e: { target: { value: string } }) => void } }> };
      toJSON: () => unknown;
      unmount: () => void;
    };
    // Latest season 2 has only Superleague, so fresh open is Superleague.
    assert.equal(selectsOf(r)[1]!.props.value, 2);
    assert.equal(selectsOf(r)[0]!.props.value, 1);

    // Go to season 1 where Feeder 2 exists, pick it.
    await act(async () => {
      selectsOf(r)[1]!.props.onChange!({ target: { value: '1' } });
    });
    await flush();
    await act(async () => {
      selectsOf(r)[0]!.props.onChange!({ target: { value: '2' } });
    });
    await flush();
    assert.equal(selectsOf(r)[0]!.props.value, 2);
    let output = JSON.stringify(r.toJSON());
    assert.ok(output.includes('Feeder Red'), 'feeder renders in season 1');

    // Season 1 -> 2: Feeder 2 is absent, so selector and standings fall back together.
    await act(async () => {
      selectsOf(r)[1]!.props.onChange!({ target: { value: '2' } });
    });
    await flush();
    assert.equal(selectsOf(r)[1]!.props.value, 2);
    assert.equal(selectsOf(r)[0]!.props.value, 1, 'missing feeder falls back to Superleague');
    output = JSON.stringify(r.toJSON());
    assert.ok(output.includes('Superleague'), 'standings show Superleague fallback');
    assert.ok(!output.includes('League not part of this season'), 'fallback never shows the invalid-league error');
    assert.ok(output.includes('Season 2'), 'headings match season 2, not stale season 1');

    // Season 2 -> 1 preserves the fallback (Superleague), not the old feeder.
    await act(async () => {
      selectsOf(r)[1]!.props.onChange!({ target: { value: '1' } });
    });
    await flush();
    assert.equal(selectsOf(r)[0]!.props.value, 1, 'fallback selection survives the next switch');
    output = JSON.stringify(r.toJSON());
    assert.ok(output.includes('Superleague'), 'still Superleague after returning to season 1');
    r.unmount();
  });

  it('rapid season switches never show a season/league mismatch', async () => {
    installFetch('stable');
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(React.createElement(StandingsPage, baseProps()));
    });
    assert.ok(renderer);
    await flush();
    const r = renderer as unknown as {
      root: { findAllByType: (t: string) => Array<{ props: { value?: unknown; onChange?: (e: { target: { value: string } }) => void } }> };
      toJSON: () => unknown;
      unmount: () => void;
    };
    // Pick feeder, then flip seasons without waiting between flips.
    await act(async () => {
      selectsOf(r)[0]!.props.onChange!({ target: { value: '2' } });
    });
    await act(async () => {
      selectsOf(r)[1]!.props.onChange!({ target: { value: '1' } });
    });
    await act(async () => {
      selectsOf(r)[1]!.props.onChange!({ target: { value: '2' } });
    });
    await flush(15);
    const league = selectsOf(r)[0]!.props.value;
    const season = selectsOf(r)[1]!.props.value;
    assert.equal(season, 2, 'ends on the last requested season');
    assert.equal(league, 2, 'preserves the feeder across rapid switches');
    const output = JSON.stringify(r.toJSON());
    // The loaded payload must match the selectors: Season 2 + Feeder Red.
    assert.ok(output.includes('Season 2'), 'headings match the final season');
    assert.ok(output.includes('Feeder Red'), 'standings match the final league');
    assert.ok(!output.includes('Season 1 · 32/32'), 'no stale season 1 progress heading');
    r.unmount();
  });
});
