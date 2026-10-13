import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  isContestedIndex,
  isQualifyingIndex,
  isWildcardQualifiedIndex,
  liveCutLineLabel,
  liveCutoffLegend,
  qualifiedRowCount,
  resolveLiveQualificationCutoff,
} from './typeCupLiveQualification.ts';
import type { TypeCupStageInfo } from './typeCupLiveModel.ts';
import type { TypeCupDraw } from '../cups/typeCupTournamentApi.ts';

function qualStage(qual: number, kind: TypeCupStageInfo['kind'] = 'qualification'): TypeCupStageInfo {
  return {
    key: { tournamentPhase: kind === 'final' ? 2 : 1, qualificationGroup: qual },
    kind,
    qualificationGroup: kind === 'qualification' ? qual : null,
    label: kind === 'final' ? 'Type Cup Final' : `Qualification Group ${qual}`,
    shortLabel: kind === 'final' ? 'Final' : `Group ${qual}`,
    status: 'active',
    played: 10,
    total: 32,
  };
}

function drawOf(groups: Array<{ qualificationGroup: number; groupSize: number; finalPlaces: number }>, extra?: Record<string, unknown>): TypeCupDraw {
  return {
    saveId: 's',
    sourceSeasonNumber: 2,
    sourceSeasonId: 7,
    rulesVersion: 1,
    tournamentFormatVersion: 2,
    isDirectFinal: false,
    teamCount: 96,
    qualificationGroupCount: groups.length,
    groupSizes: groups.map((g) => g.groupSize),
    finalPlacesPerGroup: groups.map((g) => g.finalPlaces),
    drawChecksum: 'd',
    rngBeforeState: 1,
    rngBeforeStream: 2,
    rngAfterState: 3,
    rngAfterStream: 4,
    groups: groups.map((g) => ({ ...g, creatureTypes: [] as string[] })),
    qualificationPolicyVersion: 2,
    guaranteedPlacesPerGroup: groups.map((g) => g.finalPlaces),
    wildcardCount: 2,
    ...extra,
  } as TypeCupDraw;
}

describe('MSS-080 live qualification cutoff', () => {
  it('resolves the current group quota from the draw without hard-coding 10', () => {
    const draw = drawOf([
      { qualificationGroup: 1, groupSize: 32, finalPlaces: 10 },
      { qualificationGroup: 2, groupSize: 32, finalPlaces: 10 },
      { qualificationGroup: 3, groupSize: 32, finalPlaces: 10 },
    ]);
    const cutoff = resolveLiveQualificationCutoff({ stage: qualStage(2), draw, tournament: null });
    assert.ok(cutoff);
    assert.equal(cutoff.qualificationGroup, 2);
    assert.equal(cutoff.guaranteed, 10);
    assert.equal(cutoff.isConfirmed, false);
    assert.equal(cutoff.hasWildcard, true);
    assert.equal(cutoff.hasContestedPlace, true);
    assert.equal(liveCutoffLegend(cutoff), 'Top 10 provisionally qualify · rank 11 competes for 2 wildcards across groups');
    assert.equal(liveCutLineLabel(cutoff), 'Qualification cut — top 10 provisionally qualify');
  });

  it('supports uneven per-group quotas (no constant top-N assumption)', () => {
    const draw: TypeCupDraw = {
      ...drawOf([
        { qualificationGroup: 1, groupSize: 28, finalPlaces: 11 },
        { qualificationGroup: 2, groupSize: 28, finalPlaces: 11 },
        { qualificationGroup: 3, groupSize: 28, finalPlaces: 10 },
      ]),
      qualificationPolicyVersion: 1,
      wildcardCount: 0,
      tournamentFormatVersion: 1,
    };
    const a = resolveLiveQualificationCutoff({ stage: qualStage(1), draw, tournament: null });
    const c = resolveLiveQualificationCutoff({ stage: qualStage(3), draw, tournament: null });
    assert.ok(a && c);
    assert.equal(a.guaranteed, 11);
    assert.equal(c.guaranteed, 10);
    assert.equal(a.hasWildcard, false);
    assert.equal(liveCutoffLegend(a), 'Top 11 provisionally qualify');
    assert.equal(liveCutoffLegend(c), 'Top 10 provisionally qualify');
  });

  it('prefers the completed tournament table and marks it confirmed at the final table', () => {
    const draw = drawOf([{ qualificationGroup: 1, groupSize: 32, finalPlaces: 10 }]);
    const tournament = {
      isDirectFinal: false,
      wildcardCount: 2,
      qualificationPolicyVersion: 2,
      qualificationGroups: [
        {
          qualificationGroup: 1,
          groupSize: 32,
          finalPlaces: 10,
          guaranteedPlaces: 10,
          wildcardCandidate: 'Elf',
          wildcardWinner: null,
          teams: [],
          legs: [],
          qualifiedTeams: [],
          eliminatedTeams: [],
          checksum: 'q',
        },
      ],
    } as never;
    const cutoff = resolveLiveQualificationCutoff({ stage: qualStage(1), draw, tournament, isFinalTable: true });
    assert.ok(cutoff);
    assert.equal(cutoff.isConfirmed, true);
    assert.equal(cutoff.guaranteed, 10);
    assert.equal(liveCutoffLegend(cutoff), 'Top 10 qualified · rank 11 contested 2 wildcards across groups');
    assert.equal(liveCutLineLabel(cutoff), 'Qualification cut — top 10 qualified');
  });

  it('keeps an earlier replay round of a completed group provisional, never confirmed', () => {
    const draw = drawOf([{ qualificationGroup: 1, groupSize: 32, finalPlaces: 10 }]);
    const tournament = {
      isDirectFinal: false,
      wildcardCount: 2,
      qualificationPolicyVersion: 2,
      final: { teamCount: 32, championCreatureType: 'Elf' },
      qualificationGroups: [
        {
          qualificationGroup: 1,
          groupSize: 32,
          finalPlaces: 10,
          guaranteedPlaces: 10,
          wildcardCandidate: 'Elf',
          wildcardWinner: 'Elf',
          teams: [],
          legs: [],
          qualifiedTeams: [],
          eliminatedTeams: [],
          checksum: 'q',
        },
      ],
    } as never;
    // No isFinalTable (early replay): the eventual winner must not confirm rank 11.
    for (const args of [
      { stage: qualStage(1), draw, tournament },
      { stage: qualStage(1), draw, tournament, isFinalTable: false },
    ] as never[]) {
      const cutoff = resolveLiveQualificationCutoff(args);
      assert.ok(cutoff);
      assert.equal(cutoff.isConfirmed, false);
      assert.equal(cutoff.wildcardWinner, null);
      assert.equal(cutoff.guaranteed, 10);
      assert.equal(cutoff.hasContestedPlace, true);
      assert.equal(qualifiedRowCount(cutoff), 10);
      assert.equal(isQualifyingIndex(9, cutoff), true);
      assert.equal(isQualifyingIndex(10, cutoff), false);
      assert.equal(isWildcardQualifiedIndex(10, cutoff), false);
      assert.equal(isContestedIndex(10, cutoff, 32), true);
      assert.equal(
        liveCutoffLegend(cutoff),
        'Top 10 provisionally qualify · rank 11 competes for 2 wildcards across groups',
      );
      assert.equal(liveCutLineLabel(cutoff), 'Qualification cut — top 10 provisionally qualify');
    }
  });

  it('highlights a confirmed wildcard winner as qualified, never contested', () => {
    const draw = drawOf([{ qualificationGroup: 1, groupSize: 32, finalPlaces: 10 }]);
    const tournament = {
      isDirectFinal: false,
      wildcardCount: 2,
      qualificationPolicyVersion: 2,
      final: { teamCount: 32, championCreatureType: 'Elf' },
      qualificationGroups: [
        {
          qualificationGroup: 1,
          groupSize: 32,
          finalPlaces: 10,
          guaranteedPlaces: 10,
          wildcardCandidate: 'Elf',
          wildcardWinner: 'Elf',
          teams: [],
          legs: [],
          qualifiedTeams: [],
          eliminatedTeams: [],
          checksum: 'q',
        },
      ],
    } as never;
    const cutoff = resolveLiveQualificationCutoff({ stage: qualStage(1), draw, tournament, isFinalTable: true });
    assert.ok(cutoff);
    assert.equal(cutoff.isConfirmed, true);
    assert.equal(cutoff.wildcardWinner, 'Elf');
    assert.equal(cutoff.hasContestedPlace, false);
    assert.equal(qualifiedRowCount(cutoff), 11);
    assert.equal(isQualifyingIndex(9, cutoff), true);
    assert.equal(isQualifyingIndex(10, cutoff), true);
    assert.equal(isWildcardQualifiedIndex(10, cutoff), true);
    assert.equal(isQualifyingIndex(11, cutoff), false);
    assert.equal(isContestedIndex(10, cutoff, 32), false);
    assert.equal(liveCutoffLegend(cutoff), 'Top 11 qualified (Top 10 + wildcard Elf)');
    assert.equal(liveCutLineLabel(cutoff), 'Qualification cut — top 11 qualified');
  });

  it('settles a losing confirmed group as eliminated, never still contested', () => {
    const draw = drawOf([{ qualificationGroup: 2, groupSize: 32, finalPlaces: 10 }]);
    const tournament = {
      isDirectFinal: false,
      wildcardCount: 2,
      qualificationPolicyVersion: 2,
      final: { teamCount: 32, championCreatureType: 'Goblin' },
      qualificationGroups: [
        {
          qualificationGroup: 2,
          groupSize: 32,
          finalPlaces: 10,
          guaranteedPlaces: 10,
          wildcardCandidate: 'Orc',
          wildcardWinner: null,
          teams: [],
          legs: [],
          qualifiedTeams: [],
          eliminatedTeams: [],
          checksum: 'q',
        },
      ],
    } as never;
    const cutoff = resolveLiveQualificationCutoff({ stage: qualStage(2), draw, tournament, isFinalTable: true });
    assert.ok(cutoff);
    assert.equal(cutoff.isConfirmed, true);
    assert.equal(cutoff.wildcardWinner, null);
    assert.equal(cutoff.hasContestedPlace, false);
    assert.equal(qualifiedRowCount(cutoff), 10);
    assert.equal(isQualifyingIndex(9, cutoff), true);
    assert.equal(isQualifyingIndex(10, cutoff), false);
    assert.equal(isContestedIndex(10, cutoff, 32), false);
    assert.equal(liveCutoffLegend(cutoff), 'Top 10 qualified');
    assert.equal(liveCutLineLabel(cutoff), 'Qualification cut — top 10 qualified');
  });

  it('returns null for the Final, single field and direct Finals (no misleading highlight)', () => {
    const draw = drawOf([{ qualificationGroup: 1, groupSize: 32, finalPlaces: 10 }]);
    assert.equal(resolveLiveQualificationCutoff({ stage: qualStage(0, 'final'), draw, tournament: null }), null);
    assert.equal(
      resolveLiveQualificationCutoff({
        stage: { ...qualStage(1), kind: 'single', qualificationGroup: null, key: { tournamentPhase: 0, qualificationGroup: 0 } },
        draw,
        tournament: null,
      }),
      null,
    );
    assert.equal(resolveLiveQualificationCutoff({ stage: null, draw, tournament: null }), null);
    const direct: TypeCupDraw = { ...draw, isDirectFinal: true, qualificationGroupCount: 0, groups: [] };
    assert.equal(resolveLiveQualificationCutoff({ stage: qualStage(1), draw: direct, tournament: null }), null);
    assert.equal(liveCutoffLegend(null), null);
    assert.equal(liveCutLineLabel(null), null);
  });

  it('marks qualifying and contested positions by rank, not team identity', () => {
    const draw = drawOf([{ qualificationGroup: 1, groupSize: 12, finalPlaces: 3 }]);
    const cutoff = resolveLiveQualificationCutoff({ stage: qualStage(1), draw, tournament: null });
    assert.ok(cutoff);
    assert.equal(isQualifyingIndex(0, cutoff), true);
    assert.equal(isQualifyingIndex(2, cutoff), true);
    assert.equal(isQualifyingIndex(3, cutoff), false);
    assert.equal(isContestedIndex(3, cutoff, 12), true);
    assert.equal(isContestedIndex(4, cutoff, 12), false);
    // No wildcard policy means no contested place is ever claimed.
    const plain = { ...cutoff, hasWildcard: false, hasContestedPlace: false };
    assert.equal(isContestedIndex(3, plain, 12), false);
  });

  it('handles a zero-guaranteed group without falsely marking rank 1 as qualified', () => {
    const cutoff = {
      qualificationGroup: 1,
      groupSize: 8,
      guaranteed: 0,
      wildcardCount: 2,
      hasWildcard: true,
      hasContestedPlace: true,
      policyVersion: 2,
      isConfirmed: false,
    } as never;
    assert.equal(isQualifyingIndex(0, cutoff), false);
    assert.equal(isContestedIndex(0, cutoff, 8), true);
    assert.equal(liveCutLineLabel(cutoff), null);
    assert.ok((liveCutoffLegend(cutoff) ?? '').includes('No automatic places'));
  });
});
