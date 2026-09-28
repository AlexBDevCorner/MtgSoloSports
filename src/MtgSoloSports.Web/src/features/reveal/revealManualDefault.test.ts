import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  canOptInPlay,
  resolveModeTransition,
  resolveNewRoundPresentation,
  resolveReplayPresentation,
} from './revealPlaybackPolicy.ts';
import { clampRevealed, nextRevealed } from './revealOrder.ts';

const TOTAL = 32;

describe('Live manual default (autoPlayOnStart: false)', () => {
  it('starts a newly completed round paused at 0 with no saved preference', () => {
    const state = resolveNewRoundPresentation({
      mode: 'animated',
      prefersReducedMotion: false,
      total: TOTAL,
      autoPlayOnStart: false,
    });
    assert.equal(state.revealedCount, 0);
    assert.equal(state.isPlaying, false);
  });

  it('starts paused even with a stored animated preference', () => {
    // A stored animated mode arrives as mode: 'animated' as well; it must
    // not imply autoplay permission for Live.
    const state = resolveNewRoundPresentation({
      mode: 'animated',
      prefersReducedMotion: false,
      total: TOTAL,
      autoPlayOnStart: false,
    });
    assert.equal(state.revealedCount, 0);
    assert.equal(state.isPlaying, false);
  });

  it('resets a new round to paused at 0 without inheriting prior playback', () => {
    // The policy takes no prior isPlaying input: moving from an
    // already-playing round to a new revealKey always restarts paused.
    const first = resolveNewRoundPresentation({
      mode: 'animated',
      prefersReducedMotion: false,
      total: TOTAL,
      autoPlayOnStart: false,
    });
    assert.equal(first.isPlaying, false);
    const second = resolveNewRoundPresentation({
      mode: 'animated',
      prefersReducedMotion: false,
      total: TOTAL,
      autoPlayOnStart: false,
    });
    assert.equal(second.revealedCount, 0);
    assert.equal(second.isPlaying, false);
  });

  it('preserves an explicit instant preference by showing all paused', () => {
    const state = resolveNewRoundPresentation({
      mode: 'instant',
      prefersReducedMotion: false,
      total: TOTAL,
      autoPlayOnStart: false,
    });
    assert.equal(state.revealedCount, TOTAL);
    assert.equal(state.isPlaying, false);
  });

  it('steps exactly one card per +1/-1 while staying paused', () => {
    // +1 from 0 reveals exactly one card; -1 removes one step.
    assert.equal(nextRevealed(0, TOTAL), 1);
    assert.equal(nextRevealed(1, TOTAL), 2);
    assert.equal(clampRevealed(1 - 1, TOTAL), 0);
    assert.equal(clampRevealed(2 - 1, TOTAL), 1);
  });

  it('restart rewinds to 0 without starting autoplay', () => {
    const state = resolveReplayPresentation({
      mode: 'animated',
      prefersReducedMotion: false,
      total: TOTAL,
      autoPlayOnStart: false,
    });
    assert.equal(state.revealedCount, 0);
    assert.equal(state.isPlaying, false);
  });

  it('switching from Instant back to Animated stays paused', () => {
    const state = resolveModeTransition({
      next: 'animated',
      prefersReducedMotion: false,
      total: TOTAL,
      autoPlayOnStart: false,
    });
    assert.equal(state.mode, 'animated');
    assert.equal(state.revealedCount, 0);
    assert.equal(state.isPlaying, false);
  });

  it('still offers explicit Play as opt-in autoplay', () => {
    const initial = resolveNewRoundPresentation({
      mode: 'animated',
      prefersReducedMotion: false,
      total: TOTAL,
      autoPlayOnStart: false,
    });
    // Paused at 0 with cards remaining means Play has something to start.
    assert.equal(initial.isPlaying, false);
    assert.ok(initial.revealedCount < TOTAL);
    assert.equal(canOptInPlay({ prefersReducedMotion: false, total: TOTAL }), true);
  });
});

describe('History replay preserves autoplay (autoPlayOnStart default true)', () => {
  it('autoplays a new animated round from 0', () => {
    const state = resolveNewRoundPresentation({
      mode: 'animated',
      prefersReducedMotion: false,
      total: TOTAL,
      autoPlayOnStart: true,
    });
    assert.equal(state.revealedCount, 0);
    assert.equal(state.isPlaying, true);
  });

  it('autoplays after switching to animated and after restart', () => {
    const switched = resolveModeTransition({
      next: 'animated',
      prefersReducedMotion: false,
      total: TOTAL,
      autoPlayOnStart: true,
    });
    assert.equal(switched.isPlaying, true);
    const restarted = resolveReplayPresentation({
      mode: 'animated',
      prefersReducedMotion: false,
      total: TOTAL,
      autoPlayOnStart: true,
    });
    assert.equal(restarted.revealedCount, 0);
    assert.equal(restarted.isPlaying, true);
  });

  it('never autoplays an empty round', () => {
    const state = resolveNewRoundPresentation({
      mode: 'animated',
      prefersReducedMotion: false,
      total: 0,
      autoPlayOnStart: true,
    });
    assert.equal(state.revealedCount, 0);
    assert.equal(state.isPlaying, false);
    assert.equal(canOptInPlay({ prefersReducedMotion: false, total: 0 }), false);
  });
});

describe('reduced-motion and instant regression', () => {
  it('keeps the instant/no-motion experience for reduced motion on Live', () => {
    const live = resolveNewRoundPresentation({
      mode: 'animated',
      prefersReducedMotion: true,
      total: TOTAL,
      autoPlayOnStart: false,
    });
    assert.equal(live.revealedCount, TOTAL);
    assert.equal(live.isPlaying, false);
    assert.equal(canOptInPlay({ prefersReducedMotion: true, total: TOTAL }), false);
  });

  it('keeps the instant/no-motion experience for reduced motion on History', () => {
    const history = resolveNewRoundPresentation({
      mode: 'animated',
      prefersReducedMotion: true,
      total: TOTAL,
      autoPlayOnStart: true,
    });
    assert.equal(history.revealedCount, TOTAL);
    assert.equal(history.isPlaying, false);
  });

  it('forces the accessible instant table when requesting animated with reduced motion', () => {
    for (const autoPlayOnStart of [false, true]) {
      const transition = resolveModeTransition({
        next: 'animated',
        prefersReducedMotion: true,
        total: TOTAL,
        autoPlayOnStart,
      });
      assert.equal(transition.mode, 'instant');
      assert.equal(transition.revealedCount, TOTAL);
      assert.equal(transition.isPlaying, false);
    }
  });

  it('shows all immediately for instant mode on both pages', () => {
    for (const autoPlayOnStart of [false, true]) {
      const state = resolveNewRoundPresentation({
        mode: 'instant',
        prefersReducedMotion: false,
        total: TOTAL,
        autoPlayOnStart,
      });
      assert.equal(state.revealedCount, TOTAL);
      assert.equal(state.isPlaying, false);
    }
  });
});
