import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  boardColumnCount,
  describeTile,
  tileMovementGlyph,
  tileMovementKind,
} from './revealBoardHelpers.ts';
import type { ProgressiveStandingRow } from './revealOrder.ts';

function makeRow(overrides: Partial<ProgressiveStandingRow> & { athleteId: number }): ProgressiveStandingRow {
  return {
    name: `Card ${overrides.athleteId}`,
    imageUrl: null,
    position: overrides.athleteId,
    isRevealed: false,
    baseThousandths: 0,
    awardedThousandths: 0,
    displayedScoreThousandths: 5000,
    startRank: 1,
    currentRank: 1,
    rankDelta: 0,
    finalRank: 1,
    ...overrides,
  };
}

describe('boardColumnCount', () => {
  it('uses two columns on phones', () => {
    assert.equal(boardColumnCount(320), 2);
    assert.equal(boardColumnCount(639), 2);
  });

  it('steps up through tablet and ordinary desktop widths', () => {
    assert.equal(boardColumnCount(640), 3);
    assert.equal(boardColumnCount(899), 3);
    assert.equal(boardColumnCount(900), 4);
    assert.equal(boardColumnCount(1199), 4);
    assert.equal(boardColumnCount(1200), 6);
    assert.equal(boardColumnCount(1599), 6);
  });

  it('shows eight across on sufficiently wide desktops for a 32-card 8x4 board', () => {
    assert.equal(boardColumnCount(1600), 8);
    assert.equal(boardColumnCount(1920), 8);
    assert.equal(boardColumnCount(2560), 8);
  });

  it('falls back to two columns for invalid widths', () => {
    assert.equal(boardColumnCount(Number.NaN), 2);
    assert.equal(boardColumnCount(0), 2);
    assert.equal(boardColumnCount(-10), 2);
  });
});

describe('tile movement presentation', () => {
  it('classifies direction without Lê sporting math', () => {
    assert.equal(tileMovementKind(2), 'up');
    assert.equal(tileMovementKind(-1), 'down');
    assert.equal(tileMovementKind(0), 'flat');
  });

  it('uses color-independent glyphs', () => {
    assert.equal(tileMovementGlyph(3), '▲');
    assert.equal(tileMovementGlyph(-3), '▼');
    assert.equal(tileMovementGlyph(0), '•');
  });
});

describe('describeTile', () => {
  it('shows artwork only for revealed rows with an image', () => {
    const revealed = makeRow({ athleteId: 1, isRevealed: true, imageUrl: 'https://img/1.jpg', currentRank: 1, name: 'Alpha' });
    const tile = describeTile(revealed);
    assert.equal(tile.showArtwork, true);
    assert.equal(tile.rankLabel, '#1');
    assert.ok(tile.accessibleName.includes('Alpha'));
  });

  it('keeps pending tiles face-down without artwork or awards', () => {
    const pending = makeRow({ athleteId: 2, isRevealed: false, imageUrl: 'https://img/2.jpg', currentRank: 5, name: 'Beta', awardedThousandths: 0 });
    const tile = describeTile(pending);
    assert.equal(tile.showArtwork, false);
    assert.equal(tile.isRevealed, false);
    assert.ok(tile.accessibleName.includes('face-down'));
    assert.ok(!tile.accessibleName.includes('+'));
  });

  it('uses a portrait fallback for revealed rows without an image', () => {
    const revealed = makeRow({ athleteId: 3, isRevealed: true, imageUrl: null, currentRank: 2 });
    assert.equal(describeTile(revealed).showArtwork, false);
    assert.equal(describeTile(revealed).isRevealed, true);
  });
});
