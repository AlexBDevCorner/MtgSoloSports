import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { create, act } from 'react-test-renderer';
import { LiveEventView } from './LiveEventView.tsx';
import { projectRevealedTeamStandings } from '../events/teamStandingsProjection.ts';
import { isQualifyingIndex } from './typeCupLiveQualification.ts';

// MSS-080: mount the live Type Cup standings with uneven per-group quotas and
// verify the qualification boundary follows the selected stage, never leaks,
// and never marks the Final as qualifying.

(globalThis as unknown as { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
(globalThis as unknown as { React: typeof React }).React = React;

const SAVE = '33333333-3333-3333-3333-333333333333';
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

function notFound(): unknown {
  return {
    ok: false,
    status: 404,
    statusText: 'Not Found',
    json: () => Promise.resolve({}),
    text: () => Promise.resolve('Not Found'),
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

function qualRounds(qual: number, count: number): Array<Record<string, unknown>> {
  const out: Array<Record<string, unknown>> = [];
  for (let i = 0; i < count; i += 1) {
    out.push({ group: (i % 4) + 1, round: Math.floor(i / 4) + 1, tournamentPhase: 1, qualificationGroup: qual });
  }
  return out;
}

const GROUP_A_TEAMS = [
  { teamName: 'Alpha', rank: null, scoreThousandths: 400000 },
  { teamName: 'Beta', rank: null, scoreThousandths: 300000 },
  { teamName: 'Gamma', rank: null, scoreThousandths: 200000 },
  { teamName: 'Delta', rank: null, scoreThousandths: 100000 },
];

const GROUP_B_TEAMS = [
  { teamName: 'Epsilon', rank: null, scoreThousandths: 400000 },
  { teamName: 'Zeta', rank: null, scoreThousandths: 300000 },
  { teamName: 'Eta', rank: null, scoreThousandths: 200000 },
  { teamName: 'Theta', rank: null, scoreThousandths: 100000 },
];

function installQualFetch(): void {
  const g = globalThis as unknown as Record<string, unknown>;
  const allRounds = [...qualRounds(1, 8), ...qualRounds(2, 8)];
  g['fetch'] = (input: unknown, init?: { method?: string }) => {
    const url = String(input);
    const method = (init?.method ?? 'GET').toUpperCase();
    if (method !== 'GET') {
      return Promise.resolve(okJson({}));
    }
    if (url.includes(`/history/seasons/${SEASON}/events/type-cup-team/rounds/`)) {
      const roundMatch = /\/rounds\/(\d+)/.exec(url);
      const roundNumber = roundMatch ? Number.parseInt(roundMatch[1]!, 10) : 1;
      const parsed = new URL(url, 'http://localhost');
      const group = parsed.searchParams.get('group') ? Number.parseInt(parsed.searchParams.get('group')!, 10) : 1;
      const qual = parsed.searchParams.get('qualificationGroup') ? Number.parseInt(parsed.searchParams.get('qualificationGroup')!, 10) : 1;
      return Promise.resolve(
        okJson({
          seasonNumber: SEASON,
          event: 'type-cup-team',
          title: 'Type Cup — team',
          group,
          roundNumber,
          rulesVersion: 1,
          payloadChecksum: `q${qual}-${group}-${roundNumber}-persisted`,
          placements: [],
          tournamentPhase: 1,
          qualificationGroup: qual,
        }),
      );
    }
    if (url.includes(`/history/seasons/${SEASON}/events/type-cup-team/rounds`)) {
      return Promise.resolve(okJson({ rounds: allRounds }));
    }
    if (url.includes(`/history/seasons/${SEASON}/events/type-cup-team/team-standings`)) {
      const parsed = new URL(url, 'http://localhost');
      const phase = parsed.searchParams.get('tournamentPhase');
      const qual = parsed.searchParams.get('qualificationGroup');
      if (phase === '2') {
        return Promise.resolve(
          okJson({ isFinal: false, groupsCompleted: 0, teams: GROUP_A_TEAMS.slice(0, 2), members: [] }),
        );
      }
      if (qual === '2') {
        return Promise.resolve(okJson({ isFinal: false, groupsCompleted: 0, teams: GROUP_B_TEAMS, members: [] }));
      }
      return Promise.resolve(okJson({ isFinal: false, groupsCompleted: 0, teams: GROUP_A_TEAMS, members: [] }));
    }
    if (url.includes(`/history/seasons/${SEASON}/events`)) {
      return Promise.resolve(
        okJson({
          events: [
            {
              event: 'type-cup-team',
              title: 'Type Cup — team',
              roundsPlayed: allRounds.length,
              totalRounds: 64,
              groupCount: 4,
              roundsPerGroup: 8,
              isComplete: false,
            },
          ],
        }),
      );
    }
    if (url.includes('/cups/type/tournament')) {
      return Promise.resolve(notFound());
    }
    if (url.includes('/cups/type/draw')) {
      return Promise.resolve(
        okJson({
          saveId: SAVE,
          sourceSeasonNumber: SEASON,
          sourceSeasonId: 7,
          rulesVersion: 1,
          tournamentFormatVersion: 2,
          isDirectFinal: false,
          teamCount: 64,
          qualificationGroupCount: 2,
          groupSizes: [4, 4],
          finalPlacesPerGroup: [2, 1],
          drawChecksum: 'draw',
          rngBeforeState: 1,
          rngBeforeStream: 2,
          rngAfterState: 3,
          rngAfterStream: 4,
          groups: [
            { qualificationGroup: 1, groupSize: 4, finalPlaces: 2, creatureTypes: ['Alpha', 'Beta', 'Gamma', 'Delta'] },
            { qualificationGroup: 2, groupSize: 4, finalPlaces: 1, creatureTypes: ['Epsilon', 'Zeta', 'Eta', 'Theta'] },
          ],
          qualificationPolicyVersion: 2,
          guaranteedPlacesPerGroup: [2, 1],
          wildcardCount: 1,
        }),
      );
    }
    return Promise.resolve(okJson({}));
  };
}

function installCompletedWinnerFetch(): void {
  const g = globalThis as unknown as Record<string, unknown>;
  const allRounds = [...qualRounds(1, 8), ...qualRounds(2, 8)];
  g['fetch'] = (input: unknown, init?: { method?: string }) => {
    const url = String(input);
    const method = (init?.method ?? 'GET').toUpperCase();
    if (method !== 'GET') {
      return Promise.resolve(okJson({}));
    }
    if (url.includes(`/history/seasons/${SEASON}/events/type-cup-team/rounds/`)) {
      const roundMatch = /\/rounds\/(\d+)/.exec(url);
      const roundNumber = roundMatch ? Number.parseInt(roundMatch[1]!, 10) : 1;
      const parsed = new URL(url, 'http://localhost');
      const group = parsed.searchParams.get('group') ? Number.parseInt(parsed.searchParams.get('group')!, 10) : 1;
      const qual = parsed.searchParams.get('qualificationGroup') ? Number.parseInt(parsed.searchParams.get('qualificationGroup')!, 10) : 1;
      return Promise.resolve(
        okJson({
          seasonNumber: SEASON,
          event: 'type-cup-team',
          title: 'Type Cup — team',
          group,
          roundNumber,
          rulesVersion: 1,
          payloadChecksum: `q${qual}-${group}-${roundNumber}-persisted`,
          placements: [],
          tournamentPhase: 1,
          qualificationGroup: qual,
        }),
      );
    }
    if (url.includes(`/history/seasons/${SEASON}/events/type-cup-team/rounds`)) {
      return Promise.resolve(okJson({ rounds: allRounds }));
    }
    if (url.includes(`/history/seasons/${SEASON}/events/type-cup-team/team-standings`)) {
      const parsed = new URL(url, 'http://localhost');
      const qual = parsed.searchParams.get('qualificationGroup');
      if (qual === '2') {
        return Promise.resolve(okJson({ isFinal: false, groupsCompleted: 0, teams: GROUP_B_TEAMS, members: [] }));
      }
      return Promise.resolve(okJson({ isFinal: false, groupsCompleted: 0, teams: GROUP_A_TEAMS, members: [] }));
    }
    if (url.includes(`/history/seasons/${SEASON}/events`)) {
      return Promise.resolve(
        okJson({
          events: [
            {
              event: 'type-cup-team',
              title: 'Type Cup — team',
              roundsPlayed: allRounds.length,
              totalRounds: 64,
              groupCount: 4,
              roundsPerGroup: 8,
              isComplete: false,
            },
          ],
        }),
      );
    }
    if (url.includes('/cups/type/tournament')) {
      return Promise.resolve(
        okJson({
          saveId: SAVE,
          sourceSeasonNumber: SEASON,
          sourceSeasonId: 7,
          isDirectFinal: false,
          teamCount: 8,
          qualificationGroupCount: 2,
          groupSizes: [4, 4],
          finalPlacesPerGroup: [2, 1],
          drawChecksum: 'draw',
          qualificationGroups: [
            {
              qualificationGroup: 1,
              groupSize: 4,
              finalPlaces: 2,
              guaranteedPlaces: 2,
              checksum: 'q1',
              teams: [],
              legs: [],
              qualifiedTeams: ['Alpha', 'Beta', 'Gamma'],
              eliminatedTeams: ['Delta'],
              wildcardCandidate: 'Gamma',
              wildcardWinner: 'Gamma',
            },
            {
              qualificationGroup: 2,
              groupSize: 4,
              finalPlaces: 1,
              guaranteedPlaces: 1,
              checksum: 'q2',
              teams: [],
              legs: [],
              qualifiedTeams: ['Epsilon'],
              eliminatedTeams: ['Zeta', 'Eta', 'Theta'],
              wildcardCandidate: 'Zeta',
              wildcardWinner: null,
            },
          ],
          finalists: ['Alpha', 'Beta', 'Gamma', 'Epsilon'],
          final: { teamCount: 4, championCreatureType: 'Alpha', teams: [], legs: [] },
          championCreatureType: 'Alpha',
          tournamentChecksum: 't',
          qualificationPolicyVersion: 2,
          wildcardCount: 1,
          guaranteedPlacesPerGroup: [2, 1],
          wildcards: [],
        }),
      );
    }
    if (url.includes('/cups/type/draw')) {
      return Promise.resolve(
        okJson({
          saveId: SAVE,
          sourceSeasonNumber: SEASON,
          sourceSeasonId: 7,
          rulesVersion: 1,
          tournamentFormatVersion: 2,
          isDirectFinal: false,
          teamCount: 64,
          qualificationGroupCount: 2,
          groupSizes: [4, 4],
          finalPlacesPerGroup: [2, 1],
          drawChecksum: 'draw',
          rngBeforeState: 1,
          rngBeforeStream: 2,
          rngAfterState: 3,
          rngAfterStream: 4,
          groups: [
            { qualificationGroup: 1, groupSize: 4, finalPlaces: 2, creatureTypes: ['Alpha', 'Beta', 'Gamma', 'Delta'] },
            { qualificationGroup: 2, groupSize: 4, finalPlaces: 1, creatureTypes: ['Epsilon', 'Zeta', 'Eta', 'Theta'] },
          ],
          qualificationPolicyVersion: 2,
          guaranteedPlacesPerGroup: [2, 1],
          wildcardCount: 1,
        }),
      );
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

function renderedText(renderer: ReturnType<typeof create>): string {
  return JSON.stringify((renderer as unknown as { toJSON: () => unknown }).toJSON());
}

function countOccurrences(text: string, token: string): number {
  return text.split(token).length - 1;
}

function baseProps(overrides?: Record<string, unknown>): Record<string, unknown> {
  return {
    saveId: SAVE,
    event: 'type-cup-team' as const,
    season: SEASON,
    progress: {
      event: 'type-cup-team' as const,
      sourceSeasonNumber: SEASON,
      roundsPlayed: 16,
      totalRounds: 64,
      groupCount: 4,
      roundsPerGroup: 8,
      group: 2,
      roundInGroup: 2,
      tournamentPhase: 1,
      qualificationGroup: 1,
      qualificationGroupCount: 2,
      tournamentStage: 'Qualification Group 1 of 2',
    },
    onSelectRound: () => undefined,
    onMutated: () => undefined,
    ...overrides,
  } as unknown as Record<string, unknown>;
}

describe('MSS-080 live qualification highlight', () => {
  beforeEach(() => {
    installGlobals();
  });

  afterEach(() => {
    const g = globalThis as unknown as Record<string, unknown>;
    delete g['fetch'];
  });

  it('highlights exactly the qualifying ranks with a cut line and legend in Group A', async () => {
    installQualFetch();
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(React.createElement(LiveEventView, baseProps({ urlGroup: 2, urlRound: 2, urlTournamentPhase: 1, urlQualificationGroup: 1 }) as never));
    });
    assert.ok(renderer);
    await flush();
    const text = renderedText(renderer);
    assert.ok(text.includes('Team standings in Qualification Group A'), 'stage-scoped standings heading');
    assert.equal(countOccurrences(text, 'live-qualifying-row'), 2, 'exactly two qualifying rows for Group A quota 2');
    assert.ok(text.includes('live-cut-label'), 'visible cut line between last qualifying and first nonqualifying');
    assert.ok(text.includes('Qualification cut'), 'cut line names the boundary (not color alone)');
    assert.ok(text.includes('Top 2 provisionally qualify'), 'legend names the Group A cutoff');
    assert.ok(text.includes('wildcard race'), 'contested rank 3 is marked as a wildcard race, not guaranteed');
    assert.equal(countOccurrences(text, 'live-contested-row'), 1, 'exactly one contested wildcard row');
    (renderer as unknown as { unmount: () => void }).unmount();
  });

  it('uses each group\u2019s own quota and never leaks Group A into Group B', async () => {
    installQualFetch();
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(React.createElement(LiveEventView, baseProps({ urlGroup: 2, urlRound: 2, urlTournamentPhase: 1, urlQualificationGroup: 1 }) as never));
    });
    assert.ok(renderer);
    await flush();
    let text = renderedText(renderer);
    assert.ok(text.includes('Top 2 provisionally qualify'), 'Group A legend uses quota 2');

    await act(async () => {
      (renderer as unknown as { update: (node: React.ReactNode) => void }).update(
        React.createElement(LiveEventView, baseProps({ urlGroup: 2, urlRound: 2, urlTournamentPhase: 1, urlQualificationGroup: 2 }) as never),
      );
    });
    await flush();
    text = renderedText(renderer);
    assert.ok(text.includes('Team standings in Qualification Group B'), 'standings follow the selected group');
    assert.equal(countOccurrences(text, 'live-qualifying-row'), 1, 'Group B quota 1 highlights one row, not Group A quota 2');
    assert.ok(text.includes('Top 1 provisionally qualify'), 'Group B legend uses its own quota');
    assert.ok(!text.includes('Top 2 provisionally qualify'), 'prior group cutoff does not leak');
    (renderer as unknown as { unmount: () => void }).unmount();
  });

  it('shows no qualifying highlight or legend in the Final', async () => {
    installQualFetch();
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(
        React.createElement(
          LiveEventView,
          baseProps({
            urlGroup: null,
            urlRound: null,
            urlTournamentPhase: 2,
            urlQualificationGroup: 0,
            progress: {
              event: 'type-cup-team',
              sourceSeasonNumber: SEASON,
              roundsPlayed: 16,
              totalRounds: 64,
              groupCount: 4,
              roundsPerGroup: 8,
              group: null,
              roundInGroup: null,
              tournamentPhase: 2,
              qualificationGroup: 0,
              qualificationGroupCount: 2,
              tournamentStage: 'Final',
            },
          }) as never,
        ),
      );
    });
    assert.ok(renderer);
    await flush();
    const text = renderedText(renderer);
    assert.equal(countOccurrences(text, 'live-qualifying-row'), 0, 'Final never marks top teams as qualifying');
    assert.equal(countOccurrences(text, 'live-contested-row'), 0, 'Final has no contested wildcard row');
    assert.ok(!text.includes('provisionally qualify'), 'Final shows no qualification legend');
    assert.ok(!text.includes('Qualification cut'), 'Final shows no cut line');
    (renderer as unknown as { unmount: () => void }).unmount();
  });

  it('highlights a confirmed wildcard winner as qualified at the final table, never contested', async () => {
    installCompletedWinnerFetch();
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      // Last persisted stage round (Squad #4 Round 2) fully revealed: the
      // final table may show the persisted wildcard winner as qualified.
      renderer = create(React.createElement(LiveEventView, baseProps({ urlGroup: 4, urlRound: 2, urlTournamentPhase: 1, urlQualificationGroup: 1 }) as never));
    });
    assert.ok(renderer);
    await flush();
    const text = renderedText(renderer);
    assert.ok(text.includes('Team standings in Qualification Group A'), 'stage-scoped standings heading');
    assert.equal(countOccurrences(text, 'live-qualifying-row'), 3, 'confirmed winner group highlights 2 guaranteed + wildcard');
    assert.equal(countOccurrences(text, 'live-contested-row'), 0, 'settled winner is never still contested');
    assert.ok(text.includes('Top 3 qualified'), 'legend names the confirmed total including wildcard');
    assert.ok(text.includes('wildcard Gamma'), 'legend names the confirmed wildcard winner');
    assert.ok(text.includes('Qualification cut'), 'cut line stays visible after the wildcard row');
    (renderer as unknown as { unmount: () => void }).unmount();
  });

  it('keeps an earlier replay round of a completed tournament provisional, never confirmed', async () => {
    installCompletedWinnerFetch();
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      // Early historical round: the eventual wildcard winner must not make the
      // provisional rank 3 look confirmed.
      renderer = create(React.createElement(LiveEventView, baseProps({ urlGroup: 2, urlRound: 2, urlTournamentPhase: 1, urlQualificationGroup: 1 }) as never));
    });
    assert.ok(renderer);
    await flush();
    const text = renderedText(renderer);
    assert.ok(text.includes('Team standings in Qualification Group A'), 'stage-scoped standings heading');
    assert.equal(countOccurrences(text, 'live-qualifying-row'), 2, 'early replay highlights only the 2 guaranteed places');
    assert.equal(countOccurrences(text, 'live-contested-row'), 1, 'rank 3 stays a contested wildcard race, not confirmed');
    assert.ok(text.includes('Top 2 provisionally qualify'), 'early replay legend stays provisional');
    assert.ok(text.includes('wildcard race'), 'contested rank is marked as a wildcard race');
    assert.ok(!text.includes('Top 3 qualified'), 'early replay never claims the confirmed wildcard total');
    assert.ok(!text.includes('wildcard Gamma'), 'early replay never names the eventual winner as qualified');
    (renderer as unknown as { unmount: () => void }).unmount();
  });

  it('never leaks a confirmed winner highlight into another group', async () => {
    installCompletedWinnerFetch();
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      // Final table of the losing group: only its guaranteed place qualifies.
      renderer = create(React.createElement(LiveEventView, baseProps({ urlGroup: 4, urlRound: 2, urlTournamentPhase: 1, urlQualificationGroup: 2 }) as never));
    });
    assert.ok(renderer);
    await flush();
    const text = renderedText(renderer);
    assert.ok(text.includes('Team standings in Qualification Group B'), 'standings follow the selected group');
    assert.equal(countOccurrences(text, 'live-qualifying-row'), 1, 'losing group highlights only its guaranteed place');
    assert.equal(countOccurrences(text, 'live-contested-row'), 0, 'settled losing group is eliminated, not contested');
    assert.ok(text.includes('Top 1 qualified'), 'losing group legend shows only its guaranteed cutoff');
    assert.ok(!text.includes('wildcard Gamma'), 'prior group winner never leaks into Group B');
    (renderer as unknown as { unmount: () => void }).unmount();
  });

  it('follows the provisional ranking rather than stale team identities', () => {
    const before = [
      { teamName: 'Alpha', rank: null, scoreThousandths: 100000 },
      { teamName: 'Beta', rank: null, scoreThousandths: 90000 },
      { teamName: 'Gamma', rank: null, scoreThousandths: 80000 },
    ];
    const members = [
      { athleteId: 1, teamName: 'Gamma' },
      { athleteId: 2, teamName: 'Alpha' },
    ];
    // Gamma's newly revealed athletes overtake Alpha: the highlight must move
    // with the sorted position, not stay pinned to the previous leader.
    const reordered = projectRevealedTeamStandings(before, members, [
      { athleteId: 1, finalThousandths: 50000 },
      { athleteId: 2, finalThousandths: 1000 },
    ]);
    assert.deepEqual(
      reordered.map((row) => row.teamName),
      ['Gamma', 'Alpha', 'Beta'],
    );
    const cutoff = {
      qualificationGroup: 1,
      groupSize: 3,
      guaranteed: 1,
      wildcardCount: 0,
      hasWildcard: false,
      hasContestedPlace: false,
      policyVersion: 1,
      isConfirmed: false,
    } as never;
    assert.equal(isQualifyingIndex(0, cutoff), true);
    assert.equal(reordered[0]!.teamName, 'Gamma', 'new leader takes the qualifying position');
  });
});
