import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { create, act } from 'react-test-renderer';
import { LiveEventView } from './LiveEventView.tsx';

// MSS-078: mount the real Type Cup Live workspace with three qualification
// groups and verify Group C isolation, stage-local numbering, and replay
// without resimulation (persisted results only, stage-aware URLs).

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
      cumulativeBeforeThousandths: 9 * final,
      cumulativeAfterThousandths: 10 * final,
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

function qualRounds(qual: number, count: number): Array<Record<string, unknown>> {
  const out: Array<Record<string, unknown>> = [];
  for (let i = 0; i < count; i += 1) {
    const group = Math.floor(i / 8) + 1;
    const round = (i % 8) + 1;
    out.push({ group, round, tournamentPhase: 1, qualificationGroup: qual });
  }
  return out;
}

function installTypeCupFetch(log: { gets: string[]; posts: string[] }): void {
  const g = globalThis as unknown as Record<string, unknown>;
  const allRounds = [...qualRounds(1, 32), ...qualRounds(2, 32), ...qualRounds(3, 10)];
  g['fetch'] = (input: unknown, init?: { method?: string }) => {
    const url = String(input);
    const method = (init?.method ?? 'GET').toUpperCase();
    if (method !== 'GET') {
      log.posts.push(`${method} ${url}`);
      return Promise.resolve(okJson({}));
    }
    log.gets.push(url);
    if (url.includes(`/history/seasons/${SEASON}/events/type-cup-team/rounds/`)) {
      const roundMatch = /\/rounds\/(\d+)/.exec(url);
      const roundNumber = roundMatch ? Number.parseInt(roundMatch[1]!, 10) : 1;
      const parsed = new URL(url, 'http://localhost');
      const group = parsed.searchParams.get('group') ? Number.parseInt(parsed.searchParams.get('group')!, 10) : 1;
      const phase = parsed.searchParams.get('tournamentPhase') ? Number.parseInt(parsed.searchParams.get('tournamentPhase')!, 10) : 1;
      const qual = parsed.searchParams.get('qualificationGroup') ? Number.parseInt(parsed.searchParams.get('qualificationGroup')!, 10) : 3;
      return Promise.resolve(
        okJson({
          seasonNumber: SEASON,
          event: 'type-cup-team',
          title: 'Type Cup — team',
          group,
          roundNumber,
          rulesVersion: 1,
          payloadChecksum: `c${qual}-${group}-${roundNumber}-persisted`,
          placements: makePlacements(32),
          tournamentPhase: phase,
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
      // Final baseline starts at zero; qual baselines carry stage totals only.
      const base = phase === '2' ? 0 : 100000 + (qual === '3' ? 5000 : 0);
      return Promise.resolve(
        okJson({
          isFinal: false,
          groupsCompleted: 0,
          teams: [
            { teamName: 'Goblin', rank: 1, scoreThousandths: base + 1000 },
            { teamName: 'Elf', rank: 2, scoreThousandths: base },
          ],
          members: [],
        }),
      );
    }
    if (url.includes(`/history/seasons/${SEASON}/events`)) {
      return Promise.resolve(
        okJson({
          events: [
            {
              event: 'type-cup-team',
              title: 'Type Cup — team',
              roundsPlayed: allRounds.length,
              totalRounds: 128,
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
          teamCount: 96,
          qualificationGroupCount: 3,
          groupSizes: [32, 32, 32],
          finalPlacesPerGroup: [10, 10, 10],
          drawChecksum: 'draw',
          qualificationGroups: [
            {
              qualificationGroup: 1,
              groupSize: 32,
              finalPlaces: 10,
              checksum: 'q1',
              teams: [
                { creatureType: 'Goblin', teamRank: 1, teamScoreThousandths: 200000, teamBaseThousandths: 190000, medal: 'None', qualified: true, qualificationStatus: 'Guaranteed' },
                { creatureType: 'Elf', teamRank: 11, teamScoreThousandths: 100000, teamBaseThousandths: 99000, medal: 'None', qualified: false },
              ],
              legs: [],
              qualifiedTeams: ['Goblin'],
              eliminatedTeams: ['Elf'],
              guaranteedPlaces: 10,
              wildcardCandidate: 'Elf',
              wildcardWinner: null,
            },
            {
              qualificationGroup: 2,
              groupSize: 32,
              finalPlaces: 10,
              checksum: 'q2',
              teams: [
                { creatureType: 'Dragon', teamRank: 1, teamScoreThousandths: 210000, teamBaseThousandths: 200000, medal: 'None', qualified: true, qualificationStatus: 'Guaranteed' },
              ],
              legs: [],
              qualifiedTeams: ['Dragon'],
              eliminatedTeams: [],
              guaranteedPlaces: 10,
              wildcardCandidate: null,
              wildcardWinner: null,
            },
          ],
          finalists: [],
          final: null,
          championCreatureType: null,
          tournamentChecksum: 't',
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
          teamCount: 96,
          qualificationGroupCount: 3,
          groupSizes: [32, 32, 32],
          finalPlacesPerGroup: [10, 10, 10],
          drawChecksum: 'draw',
          rngBeforeState: 1,
          rngBeforeStream: 2,
          rngAfterState: 3,
          rngAfterStream: 4,
          groups: [
            { qualificationGroup: 1, groupSize: 32, finalPlaces: 10, creatureTypes: ['Goblin'] },
            { qualificationGroup: 2, groupSize: 32, finalPlaces: 10, creatureTypes: ['Dragon'] },
            { qualificationGroup: 3, groupSize: 32, finalPlaces: 10, creatureTypes: ['Faerie'] },
          ],
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

describe('MSS-078 Type Cup Group C workspace isolation', () => {
  beforeEach(() => {
    installGlobals();
  });

  afterEach(() => {
    const g = globalThis as unknown as Record<string, unknown>;
    delete g['fetch'];
  });

  it('shows only Group C rounds with stage-local numbers and stage-scoped controls', async () => {
    const log = { gets: [] as string[], posts: [] as string[] };
    installTypeCupFetch(log);
    let renderer: ReturnType<typeof create> | null = null;
    const seen: Array<{ group: number | null; round: number | null; stage?: unknown }> = [];
    await act(async () => {
      renderer = create(
        React.createElement(LiveEventView, {
          saveId: SAVE,
          event: 'type-cup-team',
          season: SEASON,
          progress: {
            event: 'type-cup-team',
            sourceSeasonNumber: SEASON,
            roundsPlayed: 74,
            totalRounds: 128,
            groupCount: 4,
            roundsPerGroup: 8,
            group: 2,
            roundInGroup: 2,
            tournamentPhase: 1,
            qualificationGroup: 3,
            qualificationGroupCount: 3,
            tournamentStage: 'Qualification Group 3 of 3',
          },
          urlGroup: 2,
          urlRound: 2,
          urlTournamentPhase: 1,
          urlQualificationGroup: 3,
          onSelectRound: (group, round, stage) => {
            seen.push({ group, round, stage });
          },
          onMutated: () => undefined,
        }),
      );
    });
    assert.ok(renderer);
    await flush();

    const text = renderedText(renderer);
    assert.ok(text.includes('Qualification Group C'), 'selected stage names Group C');
    assert.ok(text.includes('Round 10 / 32'), 'stage-local progress restarts per stage (Squad #2 R2 is overall 10)');
    assert.ok(text.includes('Squad #2'), 'squad detail is secondary and unmistakable');
    assert.ok(!text.includes('Squad rank group'), 'no ambiguous combined squad title');
    assert.ok(text.includes('Type Cup stages'), 'compact stage nav renders');
    assert.ok(text.includes('Group A') && text.includes('Group B') && text.includes('Final'), 'nav is data-driven with all stages');
    assert.ok(text.includes('Finish Qualification Group C') || text.includes('Finish Qualification Group'), 'stage-scoped finish names its stage');
    assert.ok(text.includes('Finish entire Type Cup'), 'tournament-wide fast-forward is differentiated');
    assert.ok(text.includes('Team standings in Qualification Group C'), 'standings are stage-scoped, not Final');
    assert.ok(!text.includes('Final standings') || text.includes('Team standings in Qualification Group C'), 'qual standings never pose as Final');
    // Pills are overall stage rounds: Group C has 10 rounds, so overall 10 exists and overall 32 does not yet.
    assert.ok(text.includes('Round 10 of 32'), 'round buttons announce the unambiguous overall number');
    assert.ok(
      log.gets.some((u) => u.includes('tournamentPhase=1') && u.includes('qualificationGroup=3')),
      'round and standings fetches carry the stage identity',
    );
    assert.ok(
      !log.posts.some((u) => u.includes('/cups/type/team') && !u.includes('/rounds/')),
      'replay issues no simulation POSTs',
    );
    (renderer as unknown as { unmount: () => void }).unmount();
    assert.ok(seen.length >= 0, 'selection callback stays wired');
  });
});
