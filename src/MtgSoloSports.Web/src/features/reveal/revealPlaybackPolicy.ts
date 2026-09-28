import type { RevealMode } from './types.ts';

export interface RevealPlaybackPolicyInput {
  mode: RevealMode;
  prefersReducedMotion: boolean;
  total: number;
  /**
   * False for the Live event manual default: a new animated round starts
   * paused at 0 and mode/restart transitions never autoplay. True preserves
   * the historical autoplay-on-start behavior (History replay default).
   */
  autoPlayOnStart: boolean;
}

export interface RevealPlaybackState {
  revealedCount: number;
  isPlaying: boolean;
}

/**
 * Pure presentation policy for starting (or restarting) one immutable round.
 *
 * - `instant` (or reduced motion) always shows the full persisted table paused.
 * - `animated` with `autoPlayOnStart` autoplays from 0 (History behavior).
 * - `animated` without `autoPlayOnStart` starts paused at 0 (Live manual default).
 *
 * A stored `animated` preference never implies autoplay permission for Live;
 * only an explicit Play action may start the timer. No RNG, no sporting math.
 */
export function resolveNewRoundPresentation(input: RevealPlaybackPolicyInput): RevealPlaybackState {
  const { mode, prefersReducedMotion, total, autoPlayOnStart } = input;
  if (mode === 'instant' || prefersReducedMotion) {
    return { revealedCount: total, isPlaying: false };
  }
  if (autoPlayOnStart) {
    return { revealedCount: 0, isPlaying: total > 0 };
  }
  return { revealedCount: 0, isPlaying: false };
}

export interface RevealModeTransitionInput {
  next: RevealMode;
  prefersReducedMotion: boolean;
  total: number;
  autoPlayOnStart: boolean;
}

export interface RevealModeTransition extends RevealPlaybackState {
  mode: RevealMode;
}

/**
 * Pure policy for the Instant/Animated mode toggle.
 *
 * Switching to `animated` rewinds to 0. It autoplays only when
 * `autoPlayOnStart` allows it; Live (`false`) stays paused so the user can
 * step with +1 or press Play explicitly. Reduced motion always stays on the
 * accessible instant table.
 */
export function resolveModeTransition(input: RevealModeTransitionInput): RevealModeTransition {
  const { next, prefersReducedMotion, total, autoPlayOnStart } = input;
  if (next === 'animated' && prefersReducedMotion) {
    return { mode: 'instant', revealedCount: total, isPlaying: false };
  }
  if (next === 'instant') {
    return { mode: 'instant', revealedCount: total, isPlaying: false };
  }
  if (autoPlayOnStart) {
    return { mode: 'animated', revealedCount: 0, isPlaying: total > 0 };
  }
  return { mode: 'animated', revealedCount: 0, isPlaying: false };
}

/**
 * Whether an explicit Play action may start the timer.
 *
 * Independent of `autoPlayOnStart`: Live manual default (`false`) still
 * offers opt-in autoplay via Play whenever there is something to reveal and
 * reduced motion is off. No RNG, no sporting math.
 */
export function canOptInPlay(input: { prefersReducedMotion: boolean; total: number }): boolean {
  return !input.prefersReducedMotion && input.total > 0;
}

/**
 * Pure policy for Restart.
 *
 * Instant/reduced motion shows everything paused; animated rewinds to 0 and
 * autoplays only when `autoPlayOnStart` allows it. Live restart therefore
 * rewinds to 0 paused without secretly triggering Play.
 */
export function resolveReplayPresentation(input: RevealPlaybackPolicyInput): RevealPlaybackState {
  const { mode, prefersReducedMotion, total, autoPlayOnStart } = input;
  if (mode === 'instant' || prefersReducedMotion) {
    return { revealedCount: total, isPlaying: false };
  }
  if (autoPlayOnStart) {
    return { revealedCount: 0, isPlaying: total > 0 };
  }
  return { revealedCount: 0, isPlaying: false };
}
