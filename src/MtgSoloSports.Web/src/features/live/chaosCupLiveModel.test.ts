import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  canonicalChaosStage,
  chaosCutLabel,
  chaosGroupCount,
  chaosStageLabel,
  chaosStageShortLabel,
  chaosStageStatus,
  isChaosLivePlayable,
  isFinalChaosStage,
  nextDrawableChaosStage,
  selectedChaosGroup,
  selectedChaosRound,
  selectedChaosStage,
} from './chaosCupLiveModel.ts';
import type { ChaosCupEdition } from '../cups/chaosCupApi.ts';

function editionWith(stages: { stageIndex: number; isDrawn: boolean; isComplete: boolean; state: string }[]): ChaosCupEdition {
  return {
    saveId: 'save-1',
    sourceSeasonNumber: 2,
    sourceSeasonId: 7,
    rulesVersion: 1,
    chaosFormatVersion: 1,
    fieldSize: 1024,
    editionChecksum: 'abc',
    state: 'InProgress',
    isComplete: false,
    stageCount: 6,
    totalGroups: 63,
    totalRounds: 1008,
    roundsPlayed: 0,
    groupsCompleted: 0,
    stages: stages.map((entry) => ({
      stageIndex: entry.stageIndex,
      groupCount: 1,
      entrantCount: 32,
      isFinal: entry.stageIndex === 5,
      state: entry.state,
      isDrawn: entry.isDrawn,
      isComplete: entry.isComplete,
      drawChecksum: entry.isDrawn ? 'draw' : '',
      groupsCompleted: 0,
      roundsPlayed: 0,
      totalRounds: 16,
      standingsCount: 0,
    })),
  };
}

describe('MSS-109 Chaos Cup live model', () => {
  it('labels six stages with group counts and a distinct Final', () => {
    assert.equal(chaosGroupCount(0), 32);
    assert.equal(chaosGroupCount(5), 1);
    assert.ok(!isFinalChaosStage(0), 'stage 0 is not the Final');
    assert.ok(isFinalChaosStage(5), 'stage 5 is the Final');
    assert.equal(chaosStageLabel(0), 'Stage 1 · 32 groups · 1024 athletes');
    assert.equal(chaosStageLabel(5), 'Final · 1 group · 32 athletes');
    assert.equal(chaosStageShortLabel(0), 'S1 · 32');
    assert.equal(chaosStageShortLabel(5), 'Final');
    assert.throws(() => chaosGroupCount(6), 'stage 6 is out of range');
  });

  it('distinguishes completed, active and undrawn stages', () => {
    assert.equal(chaosStageStatus('Completed', true, true), 'completed');
    assert.equal(chaosStageStatus('InProgress', true, false), 'active');
    assert.equal(chaosStageStatus('NotDrawn', false, false), 'notDrawn');
    // A missing draw is never portrayed as complete.
    assert.equal(chaosStageStatus('Completed', false, false), 'notDrawn');
  });

  it('follows the first pending drawn stage and replays the latest drawn stage once complete', () => {
    const mixed = editionWith([
      { stageIndex: 0, isDrawn: true, isComplete: true, state: 'Completed' },
      { stageIndex: 1, isDrawn: true, isComplete: false, state: 'InProgress' },
      { stageIndex: 2, isDrawn: false, isComplete: false, state: 'NotDrawn' },
      { stageIndex: 3, isDrawn: false, isComplete: false, state: 'NotDrawn' },
      { stageIndex: 4, isDrawn: false, isComplete: false, state: 'NotDrawn' },
      { stageIndex: 5, isDrawn: false, isComplete: false, state: 'NotDrawn' },
    ]);
    assert.equal(canonicalChaosStage(mixed), 1);
    assert.equal(selectedChaosStage(null, mixed), 1, 'absent stage follows the active stage');
    assert.equal(selectedChaosStage(0, mixed), 0, 'explicit drawn stage wins for replay');
    assert.equal(selectedChaosStage(4, mixed), 4, 'explicit undrawn stage renders locked, never substituted');
    const done = editionWith(
      [0, 1, 2, 3, 4, 5].map((stageIndex) => ({ stageIndex, isDrawn: true, isComplete: true, state: 'Completed' })),
    );
    assert.equal(canonicalChaosStage(done), 5, 'a completed edition replays the Final');
    assert.equal(canonicalChaosStage(null), null, 'nothing drawn yet');
  });

  it('keeps group and round selection inside the stage without fetching the tournament', () => {
    assert.equal(selectedChaosGroup(null, 0), 1);
    assert.equal(selectedChaosGroup(33, 0), 1, 'group 33 exceeds the 32-group stage');
    assert.equal(selectedChaosGroup(16, 1), 16, 'stage 2 holds 16 groups');
    assert.equal(selectedChaosRound(null, 0), null, 'no rounds yet means no replay');
    assert.equal(selectedChaosRound(null, 5), 5, 'absent round follows the latest played round');
    assert.equal(selectedChaosRound(3, 5), 3, 'explicit round wins for replay');
  });

  it('labels the P16/P17 cut provisional until the group completes', () => {
    assert.ok(chaosCutLabel(true).includes('provisional'), 'provisional wording');
    assert.ok(chaosCutLabel(false).includes('official'), 'official wording');
    assert.ok(chaosCutLabel(true).includes('top 16 advance'), 'cut size travels with the label');
  });

  it('offers the explicit next-stage draw only once every group completes (MSS-110)', () => {
    const pending = editionWith([
      { stageIndex: 0, isDrawn: true, isComplete: false, state: 'InProgress' },
      { stageIndex: 1, isDrawn: false, isComplete: false, state: 'NotDrawn' },
      { stageIndex: 2, isDrawn: false, isComplete: false, state: 'NotDrawn' },
      { stageIndex: 3, isDrawn: false, isComplete: false, state: 'NotDrawn' },
      { stageIndex: 4, isDrawn: false, isComplete: false, state: 'NotDrawn' },
      { stageIndex: 5, isDrawn: false, isComplete: false, state: 'NotDrawn' },
    ]);
    assert.equal(nextDrawableChaosStage(pending), null, 'active stage blocks the draw');
    const ready = editionWith([
      { stageIndex: 0, isDrawn: true, isComplete: true, state: 'Completed' },
      { stageIndex: 1, isDrawn: false, isComplete: false, state: 'NotDrawn' },
      { stageIndex: 2, isDrawn: false, isComplete: false, state: 'NotDrawn' },
      { stageIndex: 3, isDrawn: false, isComplete: false, state: 'NotDrawn' },
      { stageIndex: 4, isDrawn: false, isComplete: false, state: 'NotDrawn' },
      { stageIndex: 5, isDrawn: false, isComplete: false, state: 'NotDrawn' },
    ]);
    assert.equal(nextDrawableChaosStage(ready), 1, 'completed stage unlocks the next draw');
    assert.equal(nextDrawableChaosStage(null), null, 'no edition means no draw');
  });

  it('gates scoped controls on the Dashboard legal next event (MSS-110)', () => {
    assert.equal(isChaosLivePlayable(['RunChaosCup'], 2, 2), true, 'active edition is playable');
    assert.equal(isChaosLivePlayable(['RunChaosCup'], 2, 1), false, 'historical editions replay read-only');
    assert.equal(isChaosLivePlayable(['ResolveAutomaticMovement'], 2, 2), false, 'post-Chaos phases replay read-only');
    assert.equal(isChaosLivePlayable(null, 2, 2), false, 'unknown status never simulates');
  });
});
