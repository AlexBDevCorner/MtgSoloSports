import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  cupKindOf,
  cupTitle,
  formatPoints,
  initials,
  medalBadge,
  ordinal,
  rankOf,
  reasonLabel,
  selectionKeyFor,
  stateLabel,
  teamEventKey,
  teamKeyFromName,
  teamSwatchClass,
} from './cupFormat.ts';

describe('cup display formatting', () => {
  it('formats fixed-point thousandths without doing sporting math', () => {
    assert.equal(formatPoints(123456), '123.456');
    assert.equal(formatPoints(0), '0.000');
  });

  it('labels medals and leaves non-medals blank', () => {
    assert.equal(medalBadge('Gold'), '🥇 Gold');
    assert.equal(medalBadge('Silver'), '🥈 Silver');
    assert.equal(medalBadge('Bronze'), '🥉 Bronze');
    assert.equal(medalBadge('None'), '—');
    assert.equal(medalBadge(null), '—');
  });

  it('builds ordinals including the teens', () => {
    assert.deepEqual([1, 2, 3, 4, 11, 12, 13, 21, 22, 23, 101, 111].map(ordinal), [
      '1st', '2nd', '3rd', '4th', '11th', '12th', '13th', '21st', '22nd', '23rd', '101st', '111th',
    ]);
    assert.equal(rankOf(3, 8), '3rd of 8');
  });

  it('names Cups and maps them to their events', () => {
    assert.equal(cupTitle('color'), 'Color Cup');
    assert.equal(cupTitle('type'), 'Type Cup');
    assert.equal(cupKindOf('Color'), 'color');
    assert.equal(cupKindOf('Type'), 'type');
    assert.equal(teamEventKey('color'), 'color-cup-team');
    assert.equal(teamEventKey('type'), 'type-cup-team');
    assert.equal(selectionKeyFor('color'), 'color-cup-selection');
    assert.equal(selectionKeyFor('type'), 'type-cup-selection');
  });

  it('derives team keys: lower-case for colors, exact for creature types', () => {
    assert.equal(teamKeyFromName('color', 'Multicolor'), 'multicolor');
    assert.equal(teamKeyFromName('type', 'Time Lord'), 'Time Lord');
  });

  it('gives known colors a swatch modifier and everything else the neutral swatch', () => {
    assert.equal(teamSwatchClass('color', 'red'), 'team-swatch team-swatch-red');
    assert.equal(teamSwatchClass('color', 'purple'), 'team-swatch');
    assert.equal(teamSwatchClass('type', 'red'), 'team-swatch');
  });

  it('words edition states and selection reasons', () => {
    assert.equal(stateLabel('Selected'), 'Squads selected');
    assert.equal(stateLabel('InProgress'), 'In progress');
    assert.equal(stateLabel('Completed'), 'Completed');
    assert.equal(reasonLabel('Capped'), 'Capped to this type');
    assert.equal(reasonLabel('OnlyType'), 'Only type able to field a team');
    assert.equal(reasonLabel('BestRank'), 'Ranks highest for this type');
    assert.equal(reasonLabel('Balanced'), 'Placed here so more teams take part');
    assert.equal(reasonLabel(null), null);
    assert.equal(reasonLabel('Something new'), null);
  });

  it('builds a two-letter fallback for missing card art', () => {
    assert.equal(initials('llanowar elves'), 'LL');
    assert.equal(initials('X'), 'X');
  });
});
