import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  buildTypeCupStages,
  canonicalStageKey,
  chronologicalStageRounds,
  latestPersistedStage,
  selectedStageKey,
  stageKeyEquals,
  stageLocalRound,
  stageLocalTotal,
  stageRoundAccessibleLabel,
  typeCupStageLabel,
  typeCupStageShortLabel,
  squadLabel,
} from './typeCupLiveModel.ts';

describe('MSS-078 type cup live stage model', () => {
  it('numbers stage-local rounds chronologically from squad coordinates', () => {
    assert.equal(stageLocalTotal(4, 8), 32);
    assert.equal(stageLocalRound(1, 1, 8), 1);
    assert.equal(stageLocalRound(1, 8, 8), 8);
    assert.equal(stageLocalRound(2, 1, 8), 9);
    assert.equal(stageLocalRound(4, 8, 8), 32);
  });

  it('labels qualification groups by letter and keeps squad groups distinct', () => {
    assert.equal(typeCupStageLabel({ tournamentPhase: 1, qualificationGroup: 3 }, 3, false), 'Qualification Group C');
    assert.equal(typeCupStageShortLabel({ tournamentPhase: 1, qualificationGroup: 3 }, 3, false), 'Group C');
    assert.equal(typeCupStageLabel({ tournamentPhase: 2, qualificationGroup: 0 }, 3, false), 'Type Cup Final');
    assert.equal(typeCupStageShortLabel({ tournamentPhase: 2, qualificationGroup: 0 }, 3, false), 'Final');
    assert.equal(squadLabel(2), 'Squad #2');
    assert.ok(!squadLabel(2).includes('Qualification'), 'squad labels never mention qualification');
    assert.ok(!typeCupStageShortLabel({ tournamentPhase: 1, qualificationGroup: 1 }, 3, false).includes('Squad'), 'qual labels never mention squads');
  });

  it('builds a data-driven stage list for any group count', () => {
    for (const count of [2, 3, 5]) {
      const stages = buildTypeCupStages({
        qualificationGroupCount: count,
        isDirectFinal: false,
        isLegacy: false,
        rounds: [],
        doneQualificationGroups: new Set(),
        finalComplete: false,
        activeStage: { tournamentPhase: 1, qualificationGroup: 1 },
        tournamentComplete: false,
        squadGroupCount: 4,
        roundsPerGroup: 8,
      });
      assert.equal(stages.length, count + 1);
      assert.equal(stages[stages.length - 1]!.kind, 'final');
      assert.equal(stages[0]!.qualificationGroup, 1);
      assert.equal(stages[count - 1]!.qualificationGroup, count);
    }
  });

  it('marks completed, active and pending stages from persisted facts', () => {
    const stages = buildTypeCupStages({
      qualificationGroupCount: 3,
      isDirectFinal: false,
      isLegacy: false,
      rounds: [
        ...Array.from({ length: 32 }, (_, i) => ({ group: (i >> 3) + 1, round: (i % 8) + 1, tournamentPhase: 1, qualificationGroup: 1 })),
        { group: 1, round: 1, tournamentPhase: 1, qualificationGroup: 2 },
      ],
      doneQualificationGroups: new Set([1]),
      finalComplete: false,
      activeStage: { tournamentPhase: 1, qualificationGroup: 2 },
      tournamentComplete: false,
      squadGroupCount: 4,
      roundsPerGroup: 8,
    });
    assert.equal(stages[0]!.status, 'completed');
    assert.equal(stages[1]!.status, 'active');
    assert.equal(stages[2]!.status, 'pending');
    assert.equal(stages[3]!.status, 'pending');
    assert.equal(stages[0]!.played, 32);
    assert.equal(stages[0]!.total, 32);
  });

  it('orders one stage chronologically with overall numbers alongside squad-local numbers', () => {
    const ordered = chronologicalStageRounds(
      [
        { group: 2, round: 1, tournamentPhase: 1, qualificationGroup: 3 },
        { group: 1, round: 8, tournamentPhase: 1, qualificationGroup: 3 },
        { group: 1, round: 1, tournamentPhase: 1, qualificationGroup: 2 },
      ],
      { tournamentPhase: 1, qualificationGroup: 3 },
      8,
    );
    assert.deepEqual(ordered.map((e) => e.overall), [8, 9]);
    const label = stageRoundAccessibleLabel({ overall: 9, total: 32, squadGroup: 2, roundInGroup: 1, stageLabel: 'Qualification Group C' });
    assert.ok(label.includes('Round 9 of 32'), 'overall stage round is unambiguous');
    assert.ok(label.includes('Squad #2'), 'squad-local number stays secondary');
    assert.ok(label.includes('Qualification Group C'), 'stage identity travels with the round');
  });

  it('follows the canonical stage but honours an explicit replay selection', () => {
    const rounds = [
      { group: 4, round: 8, tournamentPhase: 1, qualificationGroup: 1 },
      { group: 1, round: 1, tournamentPhase: 1, qualificationGroup: 2 },
    ];
    const canonical = canonicalStageKey({
      complete: false,
      progressStage: { tournamentPhase: 1, qualificationGroup: 2 },
      rounds,
    });
    assert.ok(stageKeyEquals(canonical, { tournamentPhase: 1, qualificationGroup: 2 }));
    const stages = buildTypeCupStages({
      qualificationGroupCount: 2,
      isDirectFinal: false,
      isLegacy: false,
      rounds,
      doneQualificationGroups: new Set([1]),
      finalComplete: false,
      activeStage: canonical,
      tournamentComplete: false,
      squadGroupCount: 4,
      roundsPerGroup: 8,
    });
    const follow = selectedStageKey({ urlStage: null, canonicalStage: canonical, stages, rounds });
    assert.ok(stageKeyEquals(follow, canonical));
    const replay = selectedStageKey({
      urlStage: { tournamentPhase: 1, qualificationGroup: 1 },
      canonicalStage: canonical,
      stages,
      rounds,
    });
    assert.ok(stageKeyEquals(replay, { tournamentPhase: 1, qualificationGroup: 1 }), 'explicit replay wins');
    assert.equal(latestPersistedStage(rounds)?.qualificationGroup, 2);
  });

  it('keeps direct-final and legacy saves on a single stage without qual navigation', () => {
    const direct = buildTypeCupStages({
      qualificationGroupCount: 0,
      isDirectFinal: true,
      isLegacy: false,
      rounds: [],
      doneQualificationGroups: new Set(),
      finalComplete: false,
      activeStage: { tournamentPhase: 2, qualificationGroup: 0 },
      tournamentComplete: false,
      squadGroupCount: 4,
      roundsPerGroup: 8,
    });
    assert.equal(direct.length, 1);
    assert.equal(direct[0]!.kind, 'final');
    const legacy = buildTypeCupStages({
      qualificationGroupCount: 0,
      isDirectFinal: false,
      isLegacy: true,
      rounds: [],
      doneQualificationGroups: new Set(),
      finalComplete: false,
      activeStage: { tournamentPhase: 0, qualificationGroup: 0 },
      tournamentComplete: false,
      squadGroupCount: 4,
      roundsPerGroup: 8,
    });
    assert.equal(legacy.length, 1);
    assert.equal(legacy[0]!.kind, 'single');
  });
});
