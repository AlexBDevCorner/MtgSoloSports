import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { colorTeamKeyFromName, colorTeamKeyFromSportingColor, cupTeamKey } from './cupTeamKeys.ts';

describe('MSS-139 cup team keys', () => {
  it('maps numeric sporting colors to lower-case team keys', () => {
    assert.equal(colorTeamKeyFromSportingColor(0), 'white');
    assert.equal(colorTeamKeyFromSportingColor(1), 'blue');
    assert.equal(colorTeamKeyFromSportingColor(2), 'black');
    assert.equal(colorTeamKeyFromSportingColor(3), 'red');
    assert.equal(colorTeamKeyFromSportingColor(4), 'green');
    assert.equal(colorTeamKeyFromSportingColor(5), 'multicolor');
    assert.equal(colorTeamKeyFromSportingColor(6), 'hybrid');
    assert.equal(colorTeamKeyFromSportingColor(7), 'colorless');
  });

  it('returns null for missing or out-of-range sporting colors', () => {
    assert.equal(colorTeamKeyFromSportingColor(null), null);
    assert.equal(colorTeamKeyFromSportingColor(undefined), null);
    assert.equal(colorTeamKeyFromSportingColor(99), 'color99');
  });

  it('maps known color names case-insensitively and rejects unknown names', () => {
    assert.equal(colorTeamKeyFromName('White'), 'white');
    assert.equal(colorTeamKeyFromName('  BLUE  '), 'blue');
    assert.equal(colorTeamKeyFromName('Colorless'), 'colorless');
    assert.equal(colorTeamKeyFromName('Dragon'), null);
    assert.equal(colorTeamKeyFromName(''), null);
    assert.equal(colorTeamKeyFromName(null), null);
  });

  it('resolves color teams by name and type teams verbatim', () => {
    assert.equal(cupTeamKey('color', 'White'), 'white');
    assert.equal(cupTeamKey('color', 'Dragon'), null);
    assert.equal(cupTeamKey('type', 'Dragon'), 'Dragon');
    assert.equal(cupTeamKey('type', '  Faerie  '), 'Faerie');
    assert.equal(cupTeamKey('type', ''), null);
    assert.equal(cupTeamKey('type', null), null);
  });
});
