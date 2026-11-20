import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { create, act } from 'react-test-renderer';
import { MovementReveal } from './MovementReveal.tsx';
import type { MovementBoundary, MovementStep } from './movementModel.ts';

// MSS-146 mounted coverage: the boundary board renders as a keyed list. The
// boundary fragment (not its inner elements) is the array item, so React must
// never warn about a missing "key" prop during a multi-boundary reveal.

(globalThis as unknown as { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
(globalThis as unknown as { React: typeof React }).React = React;

function installGlobals(): void {
  const g = globalThis as unknown as Record<string, unknown>;
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

function makeStep(id: number, direction: 'promoted' | 'relegated', feeder: string): MovementStep {
  return {
    key: `${direction}:${id}`,
    athleteId: id,
    name: `Athlete ${id}`,
    sportingColor: 'White',
    fromLeagueName: direction === 'promoted' ? feeder : 'Superleague',
    fromLeagueKey: null,
    fromSeasonRank: id,
    toLeagueName: direction === 'promoted' ? 'Superleague' : feeder,
    toLeagueKey: null,
    direction,
    imageUrl: null,
  };
}

function makeBoundary(feeder: string, label: string | null, seed: number): MovementBoundary {
  const relegated = [makeStep(seed, 'relegated', feeder), makeStep(seed + 1, 'relegated', feeder)];
  const promoted = [makeStep(seed + 2, 'promoted', feeder), makeStep(seed + 3, 'promoted', feeder)];
  return {
    key: feeder,
    feederLeagueName: feeder,
    superleagueName: 'Superleague',
    promoted,
    relegated,
    steps: [...relegated, ...promoted],
    total: 4,
    boundaryLabel: label,
    qualifierNote: null,
  };
}

describe('MSS-146 MovementReveal boundary list rendering', () => {
  beforeEach(() => {
    installGlobals();
  });

  afterEach(() => {
    const g = globalThis as unknown as Record<string, unknown>;
    delete g['fetch'];
  });

  it('renders multi-boundary boards without React key warnings', async () => {
    const boundaries = [
      makeBoundary('White League', 'Feeder 1 ↔ Superleague', 1),
      makeBoundary('Blue League', 'Feeder 1 ↔ Superleague', 5),
      makeBoundary('Red League', null, 9),
    ];
    const errors: unknown[][] = [];
    const originalError = console.error;
    console.error = (...args: unknown[]) => {
      errors.push(args);
    };
    let renderer: ReturnType<typeof create> | null = null;
    try {
      await act(async () => {
        renderer = create(
          React.createElement(MovementReveal, {
            boundaries,
            revealKey: 'movement-test:season-2',
            title: 'Season 2 → Season 3',
            meta: '12 movements',
          }),
        );
      });
      assert.ok(renderer);
      await act(async () => {
        await new Promise((resolve) => setTimeout(resolve, 0));
      });
    } finally {
      console.error = originalError;
    }
    const keyWarnings = errors.filter((args) =>
      args.some((arg) => typeof arg === 'string' && arg.includes('"key" prop')),
    );
    assert.equal(
      keyWarnings.length,
      0,
      `no React key warnings from the boundary list, got: ${JSON.stringify(keyWarnings)}`,
    );
    const text = JSON.stringify((renderer as unknown as { toJSON: () => unknown }).toJSON());
    assert.ok(text.includes('White League'), 'first boundary renders');
    assert.ok(text.includes('Blue League'), 'second boundary renders');
    assert.ok(text.includes('Red League'), 'third boundary renders');
    assert.ok(text.includes('PROMOTED') && text.includes('RELEGATED'), 'movement facts render');
    (renderer as unknown as { unmount: () => void }).unmount();
  });
});
