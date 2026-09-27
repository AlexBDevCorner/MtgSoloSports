import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  buildRevealOrder,
  clampRevealed,
  computeProgressiveStandings,
  indexPlacementsByAthlete,
  isValidRevealSpeed,
  speedToIntervalMs,
} from './revealOrder';
import type { RevealMode, RevealPlacement, RevealSpeed } from './types';

const MODE_KEY = 'mtg-solo-sports:reveal-mode';
const LEGACY_MODE_KEY = 'mtg-solo-sports:live-mode';
const SPEED_KEY = 'mtg-solo-sports:reveal-speed';

function readStored(key: string): string | null {
  try {
    const value = localStorage.getItem(key);
    return value && value.length > 0 ? value : null;
  } catch {
    return null;
  }
}

function writeStored(key: string, value: string): void {
  try {
    localStorage.setItem(key, value);
  } catch {
    // storage is best-effort; reveal still works in memory
  }
}

function readInitialMode(prefersReducedMotion: boolean): RevealMode {
  const stored = readStored(MODE_KEY) ?? readStored(LEGACY_MODE_KEY);
  if (stored === 'instant' || stored === 'animated') {
    // Reduced motion always wins over a stored animated preference.
    if (prefersReducedMotion && stored === 'animated') {
      return 'instant';
    }
    return stored;
  }
  return prefersReducedMotion ? 'instant' : 'animated';
}

function readInitialSpeed(): RevealSpeed {
  const stored = readStored(SPEED_KEY);
  return isValidRevealSpeed(stored) ? stored : 'normal';
}

function detectReducedMotion(): boolean {
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') {
    return false;
  }
  return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
}

export interface RoundRevealState {
  mode: RevealMode;
  speed: RevealSpeed;
  intervalMs: number;
  isPlaying: boolean;
  revealedCount: number;
  total: number;
  isComplete: boolean;
  revealOrder: RevealPlacement[];
  revealedFeed: RevealPlacement[];
  standings: ReturnType<typeof computeProgressiveStandings>;
  prefersReducedMotion: boolean;
  setMode: (mode: RevealMode) => void;
  setSpeed: (speed: RevealSpeed) => void;
  play: () => void;
  pause: () => void;
  togglePlay: () => void;
  replay: () => void;
  showAll: () => void;
  stepForward: () => void;
  stepBack: () => void;
}

function resolveReducedMotion(initial: boolean): boolean {
  try {
    return detectReducedMotion() || initial;
  } catch {
    return initial;
  }
}

/**
 * Local-only animation state for one immutable round result.
 *
 * The hook consumes `placements` (already persisted backend facts) and never
 * calls simulation endpoints, consumes RNG, or mutates sporting state.
 * Refreshing, changing speed, pausing or replaying only re-derives the
 * presentation from the same props. Mode/speed preferences persist to
 * localStorage as presentation-only settings.
 */
export function useRoundReveal(
  placements: readonly RevealPlacement[],
  revealKey: string,
  initialReducedMotion = false,
): RoundRevealState {
  const [prefersReducedMotion, setPrefersReducedMotion] = useState<boolean>(() =>
    resolveReducedMotion(initialReducedMotion),
  );
  const [mode, setModeState] = useState<RevealMode>(() =>
    readInitialMode(resolveReducedMotion(initialReducedMotion)),
  );
  const [speed, setSpeedState] = useState<RevealSpeed>(() => readInitialSpeed());
  const [revealedCount, setRevealedCount] = useState(0);
  const [isPlaying, setIsPlaying] = useState(false);

  const revealOrder = useMemo(() => buildRevealOrder(placements), [placements]);
  const byAthlete = useMemo(() => indexPlacementsByAthlete(placements), [placements]);
  const total = revealOrder.length;

  // Track the OS reduced-motion preference live; switching to reduced motion
  // pauses the show and drops to the instant alternative for accessibility.
  useEffect(() => {
    if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') {
      return;
    }
    const query = window.matchMedia('(prefers-reduced-motion: reduce)');
    const onChange = (event: MediaQueryListEvent) => {
      setPrefersReducedMotion(event.matches);
      if (event.matches) {
        setIsPlaying(false);
        setModeState('instant');
        writeStored(MODE_KEY, 'instant');
      }
    };
    setPrefersReducedMotion(query.matches);
    if (query.matches) {
      setModeState('instant');
      setIsPlaying(false);
    }
    query.addEventListener('change', onChange);
    return () => {
      query.removeEventListener('change', onChange);
    };
  }, []);

  // A new round payload restarts the presentation from the beginning. Instant
  // mode shows everything; animated mode starts playing from the first card
  // unless reduced motion forces the instant alternative.
  useEffect(() => {
    if (mode === 'instant' || prefersReducedMotion) {
      setRevealedCount(total);
      setIsPlaying(false);
    } else {
      setRevealedCount(0);
      setIsPlaying(total > 0);
    }
    // Restart only when the round identity or total changes by key intent;
    // mode/reduced-motion are applied deliberately, not on every keystroke.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [revealKey, total]);

  // Instant mode always shows the full persisted table with no timer.
  useEffect(() => {
    if (mode === 'instant') {
      setRevealedCount(total);
      setIsPlaying(false);
    } else if (!prefersReducedMotion && total > 0 && revealedCount >= total) {
      setIsPlaying(false);
    }
  }, [mode, prefersReducedMotion, total, revealedCount]);

  // The timer only advances local presentation state. It never fetches,
  // resimulates, or writes sporting state.
  useEffect(() => {
    if (mode !== 'animated' || !isPlaying || prefersReducedMotion) {
      return;
    }
    if (revealedCount >= total) {
      setIsPlaying(false);
      return;
    }
    const timer = window.setTimeout(
      () => {
        setRevealedCount((value) => {
          const next = clampRevealed(value + 1, total);
          if (next >= total) {
            setIsPlaying(false);
          }
          return next;
        });
      },
      speedToIntervalMs(speed),
    );
    return () => {
      window.clearTimeout(timer);
    };
  }, [mode, isPlaying, prefersReducedMotion, revealedCount, total, speed]);

  const setMode = useCallback(
    (next: RevealMode) => {
      if (next === 'animated' && prefersReducedMotion) {
        // Minimal-motion alternative: stay on the accessible instant table.
        setModeState('instant');
        writeStored(MODE_KEY, 'instant');
        return;
      }
      setModeState(next);
      writeStored(MODE_KEY, next);
      if (next === 'instant') {
        setRevealedCount(total);
        setIsPlaying(false);
      } else {
        setRevealedCount(0);
        setIsPlaying(total > 0 && !prefersReducedMotion);
      }
    },
    [prefersReducedMotion, total],
  );

  const setSpeed = useCallback((next: RevealSpeed) => {
    setSpeedState(next);
    writeStored(SPEED_KEY, next);
  }, []);

  const play = useCallback(() => {
    if (prefersReducedMotion || total === 0) {
      return;
    }
    if (mode === 'instant') {
      setModeState('animated');
      writeStored(MODE_KEY, 'animated');
    }
    setRevealedCount((value) => (value >= total ? 0 : value));
    setIsPlaying(true);
  }, [prefersReducedMotion, total, mode]);

  const pause = useCallback(() => {
    setIsPlaying(false);
  }, []);

  const togglePlay = useCallback(() => {
    if (isPlaying) {
      setIsPlaying(false);
      return;
    }
    if (prefersReducedMotion) {
      return;
    }
    if (revealedCount >= total) {
      setRevealedCount(0);
    }
    setModeState('animated');
    writeStored(MODE_KEY, 'animated');
    setIsPlaying(true);
  }, [isPlaying, prefersReducedMotion, revealedCount, total]);

  const replay = useCallback(() => {
    if (mode === 'instant' || prefersReducedMotion) {
      setRevealedCount(total);
      setIsPlaying(false);
      return;
    }
    setRevealedCount(0);
    setIsPlaying(total > 0);
  }, [mode, prefersReducedMotion, total]);

  const showAll = useCallback(() => {
    setRevealedCount(total);
    setIsPlaying(false);
  }, [total]);

  const stepForward = useCallback(() => {
    setIsPlaying(false);
    setRevealedCount((value) => clampRevealed(value + 1, total));
  }, [total]);

  const stepBack = useCallback(() => {
    setIsPlaying(false);
    setRevealedCount((value) => clampRevealed(value - 1, total));
  }, [total]);

  const standings = useMemo(
    () => computeProgressiveStandings(byAthlete, revealOrder, revealedCount),
    [byAthlete, revealOrder, revealedCount],
  );

  const revealedFeed = useMemo(
    () => revealOrder.slice(0, clampRevealed(revealedCount, total)),
    [revealOrder, revealedCount, total],
  );

  return {
    mode,
    speed,
    intervalMs: speedToIntervalMs(speed),
    isPlaying,
    revealedCount: clampRevealed(revealedCount, total),
    total,
    isComplete: total > 0 && revealedCount >= total,
    revealOrder,
    revealedFeed,
    standings,
    prefersReducedMotion,
    setMode,
    setSpeed,
    play,
    pause,
    togglePlay,
    replay,
    showAll,
    stepForward,
    stepBack,
  };
}
