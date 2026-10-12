import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import React from 'react';
import { create, act } from 'react-test-renderer';
import { StandingsPage } from './StandingsPage';

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = join(here, '..', '..');

function read(rel: string): string {
  return readFileSync(join(webRoot, rel), 'utf8');
}

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

const SAVE = 'scope-save';

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
      stageNumber: 1,
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

function inProgressSeasons(): JsonMap {
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

function completedSeasons(): JsonMap {
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
        isComplete: false,
        competitionCount: 2,
        completedStages: 1,
        totalRounds: 16,
      },
    ],
  } as unknown as JsonMap;
}

function multiTierSeasons(): JsonMap {
  return {
    saveId: SAVE,
    seasons: [
      {
        seasonNumber: 1,
        hasSuperleague: true,
        isComplete: false,
        competitionCount: 4,
        completedStages: 1,
        totalRounds: 16,
      },
    ],
  } as unknown as JsonMap;
}

function twoFeederCompetitions(): JsonMap {
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

function completedCompetitions(): JsonMap {
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

function multiTierCompetitions(seasonNumber = 1): JsonMap {
  return {
    saveId: SAVE,
    seasonNumber,
    isSeasonComplete: false,
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
        completedStages: 1,
        totalRounds: 16,
        hasFinalTable: false,
      },
      {
        leagueId: 2,
        name: 'Feeder One',
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
        leagueId: 3,
        name: 'Feeder Two',
        kind: 'Feeder',
        feederDivision: 2,
        leagueLevel: 'Feeder2',
        sportingColor: 1,
        sportingColorName: 'Blue',
        stageCount: 32,
        completedStages: 1,
        totalRounds: 16,
        hasFinalTable: false,
      },
      {
        leagueId: 4,
        name: 'Feeder Three',
        kind: 'Feeder',
        feederDivision: 3,
        leagueLevel: 'Feeder3',
        sportingColor: 2,
        sportingColorName: 'Green',
        stageCount: 32,
        completedStages: 1,
        totalRounds: 16,
        hasFinalTable: false,
      },
    ],
  } as unknown as JsonMap;
}

function stagesFor(leagueId: number, leagueName: string, complete = false): JsonMap {
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
        completedRounds: complete ? 16 : 1,
        roundsPerStage: 16,
        isComplete: complete,
        hasStandings: complete,
        roundCount: complete ? 16 : 1,
      },
    ],
  } as unknown as JsonMap;
}

function placementsFor(
  leagueId: number,
  leagueName: string,
  kind = 'Feeder',
  level: string | null = 'Feeder1',
  complete = false,
): JsonMap {
  return {
    saveId: SAVE,
    seasonNumber: 1,
    leagueId,
    leagueName,
    leagueKind: kind,
    feederDivision: kind === 'Superleague' ? 0 : 1,
    leagueLevel: level,
    isSeasonComplete: complete,
    completedStages: complete ? 32 : 1,
    athletes: athleteRows(),
    placements: placementCells(),
  } as unknown as JsonMap;
}

function placementsForTier(
  leagueId: number,
  leagueName: string,
  kind: string,
  level: string,
  division: number,
): JsonMap {
  return {
    saveId: SAVE,
    seasonNumber: 1,
    leagueId,
    leagueName,
    leagueKind: kind,
    feederDivision: division,
    leagueLevel: level,
    isSeasonComplete: false,
    completedStages: 1,
    athletes: athleteRows(),
    placements: placementCells(),
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
    standings: tableRows(),
  } as unknown as JsonMap;
}

function historyTableFor(leagueId: number, leagueName: string, complete = true): JsonMap {
  return {
    saveId: SAVE,
    seasonNumber: 1,
    leagueId,
    leagueName,
    isSeasonComplete: complete,
    seasonChecksum: 'final123',
    standings: tableRows(),
  } as unknown as JsonMap;
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

function installFetch(impl: (url: string) => JsonMap | null): void {
  const g = globalThis as unknown as Record<string, unknown>;
  g['fetch'] = (input: unknown) => {
    const url = String(input);
    if (
      url.includes('/superleague/inaugural') ||
      url.includes('/superleague/automatic-movement') ||
      url.includes('/feeder-movements') ||
      url.includes('/superleague/rebalance') ||
      url.includes('/qualifiers')
    ) {
      return Promise.resolve(errJson(404, 'not found'));
    }
    const data = impl(url);
    if (data === null) {
      return Promise.resolve(errJson(404, 'not found'));
    }
    return Promise.resolve(okJson(data));
  };
}

function assertStandingsOnly(output: string, context: string): void {
  assert.ok(!output.includes('Promotion &amp; relegation'), `${context} has no movement title`);
  assert.ok(!output.includes('Promotion & relegation'), `${context} has no movement title`);
  assert.ok(!output.includes('Feeder rebalance'), `${context} has no rebalance title`);
  assert.ok(!output.includes('league movements'), `${context} has no movement body`);
  assert.ok(!output.includes('feeder rebalance'), `${context} has no rebalance body`);
  assert.ok(!output.includes('Relegation'), `${context} has no relegation badge`);
  assert.ok(!output.includes('Qualifier'), `${context} has no qualifier badge`);
  assert.ok(!output.includes('auto-promoted'), `${context} has no promotion badge`);
}

describe('MSS-077 standings contain standings only', () => {
  it('keeps league tables and controls while removing movement, rebalance and zone copy', () => {
    const page = read('features/standings/StandingsPage.tsx');
    // Standings-specific content stays.
    assert.ok(page.includes('Season table'), 'season table remains');
    assert.ok(page.includes('Stage placements'), 'placements matrix remains');
    assert.ok(page.includes('Standings filters'), 'league/season/view filters remain');
    assert.ok(page.includes('Champ pts'), 'standings statistics remain');
    assert.ok(page.includes('Season rank'), 'sorting remains');
    assert.ok(page.includes('groupLeaguesByTier'), 'Superleague and feeder tiers remain grouped');
    assert.ok(page.includes('AthleteLink'), 'athlete names stay linked');
    // Movement / rebalance surfaces are gone.
    assert.ok(!page.includes('MovementSection'), 'no movement section import or usage');
    assert.ok(!page.includes('RebalanceSection'), 'no rebalance section import or usage');
    assert.ok(!page.includes('Promotion & relegation'), 'no promotion/relegation copy');
    assert.ok(!page.includes('Feeder rebalance'), 'no feeder-rebalance copy');
    assert.ok(!page.includes('zoneLabelForRank'), 'no promotion/relegation zone badges');
    assert.ok(!page.includes('zoneSummaryLine'), 'no zone footnote');
    assert.ok(!page.includes('Zones are visual only'), 'no zone explanatory copy');
    assert.ok(!page.includes('<th scope="col">Zone</th>'), 'no Zone table column');
  });

  it('leaves dedicated movement and rebalance workflows intact on Live/Dashboard', () => {
    const view = read('features/transitions/TransitionRevealView.tsx');
    assert.ok(view.includes('<MovementReveal'), 'Live keeps the movement reveal');
    assert.ok(view.includes('<RebalanceReveal'), 'Live keeps the rebalance reveal');
    assert.ok(view.includes('fetchAutomaticMovement'), 'Live still reads persisted movement');
    assert.ok(view.includes('fetchRebalanceResult'), 'Live still reads persisted rebalance');
    const flow = read('features/dashboard/SeasonFlow.tsx');
    assert.ok(flow.includes('advanceToNextEvent'), 'Dashboard still runs the postseason');
    assert.ok(view.includes('dashboardPath'), 'reveal still continues on the Dashboard');
  });
});

describe('MSS-077 standings rendering across seasons and tiers', () => {
  beforeEach(() => {
    installGlobals();
  });

  afterEach(() => {
    const g = globalThis as unknown as Record<string, unknown>;
    delete g['fetch'];
  });

  it('renders an in-progress season table without movement or rebalance', async () => {
    installFetch((url) => {
      if (url === `/api/saves/${SAVE}/history/seasons`) {
        return inProgressSeasons();
      }
      if (url === `/api/saves/${SAVE}/history/seasons/1/competitions`) {
        return twoFeederCompetitions();
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
    const output = JSON.stringify((renderer as unknown as { toJSON: () => unknown }).toJSON());
    assert.ok(output.includes('Card A'), 'in-progress rows render');
    assert.ok(output.includes('Champ pts'), 'statistics header renders');
    assert.ok(output.includes('Season table'), 'season table card renders');
    assertStandingsOnly(output, 'in-progress season');
    (renderer as unknown as { unmount: () => void }).unmount();
  });

  it('renders a completed season final table without movement or rebalance', async () => {
    installFetch((url) => {
      if (url === `/api/saves/${SAVE}/history/seasons`) {
        return completedSeasons();
      }
      if (url === `/api/saves/${SAVE}/history/seasons/1/competitions`) {
        return completedCompetitions();
      }
      if (url === `/api/saves/${SAVE}/history/seasons/2/competitions`) {
        return completedCompetitions();
      }
      if (url.includes('/competitions/1/stages')) {
        return stagesFor(1, 'Superleague', true);
      }
      if (url.includes('/competitions/2/stages')) {
        return stagesFor(2, 'Feeder Red', true);
      }
      if (url.includes('/competitions/1/placements')) {
        return placementsFor(1, 'Superleague', 'Superleague', 'Superleague', true);
      }
      if (url.includes('/competitions/2/placements')) {
        return placementsFor(2, 'Feeder Red', 'Feeder', 'Feeder1', true);
      }
      if (url.includes('/competitions/1/table')) {
        return historyTableFor(1, 'Superleague', true);
      }
      if (url.includes('/competitions/2/table')) {
        return historyTableFor(2, 'Feeder Red', true);
      }
      return null;
    });
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(
        React.createElement(StandingsPage, { ...baseProps(), urlSeason: 1, urlLeagueId: 1 }),
      );
    });
    assert.ok(renderer);
    await flush();
    const output = JSON.stringify((renderer as unknown as { toJSON: () => unknown }).toJSON());
    assert.ok(output.includes('Card A'), 'completed final rows render');
    assert.ok(output.includes('final'), 'completed table is marked final');
    assertStandingsOnly(output, 'completed season');
    (renderer as unknown as { unmount: () => void }).unmount();
  });

  it('renders a multi-feeder-tier universe without movement or rebalance', async () => {
    installFetch((url) => {
      if (url === `/api/saves/${SAVE}/history/seasons`) {
        return multiTierSeasons();
      }
      if (url === `/api/saves/${SAVE}/history/seasons/1/competitions`) {
        return multiTierCompetitions(1);
      }
      if (url.includes('/competitions/1/stages')) {
        return stagesFor(1, 'Superleague');
      }
      if (url.includes('/competitions/2/stages')) {
        return stagesFor(2, 'Feeder One');
      }
      if (url.includes('/competitions/3/stages')) {
        return stagesFor(3, 'Feeder Two');
      }
      if (url.includes('/competitions/4/stages')) {
        return stagesFor(4, 'Feeder Three');
      }
      if (url.includes('/competitions/1/placements')) {
        return placementsForTier(1, 'Superleague', 'Superleague', 'Superleague', 0);
      }
      if (url.includes('/competitions/2/placements')) {
        return placementsForTier(2, 'Feeder One', 'Feeder', 'Feeder1', 1);
      }
      if (url.includes('/competitions/3/placements')) {
        return placementsForTier(3, 'Feeder Two', 'Feeder', 'Feeder2', 2);
      }
      if (url.includes('/competitions/4/placements')) {
        return placementsForTier(4, 'Feeder Three', 'Feeder', 'Feeder3', 3);
      }
      if (url.includes('/standings/current')) {
        return currentFor(1, 'Superleague');
      }
      return null;
    });
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(React.createElement(StandingsPage, baseProps()));
    });
    assert.ok(renderer);
    await flush();
    const output = JSON.stringify((renderer as unknown as { toJSON: () => unknown }).toJSON());
    assert.ok(output.includes('Superleague'), 'Superleague tier renders');
    assert.ok(output.includes('Feeder 1'), 'Feeder 1 tier renders');
    assert.ok(output.includes('Feeder 2'), 'Feeder 2 tier renders');
    assert.ok(output.includes('Feeder 3'), 'Feeder 3 tier renders');
    assert.ok(output.includes('Card A'), 'multi-tier rows render');
    assertStandingsOnly(output, 'multi-tier universe');
    (renderer as unknown as { unmount: () => void }).unmount();
  });
});
