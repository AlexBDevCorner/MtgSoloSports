import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const read = (file: string) => readFileSync(join(here, file), 'utf8');

const page = read('AthleteProfilePage.tsx');
const api = read('athleteApi.ts');

describe('athlete cup history', () => {
  it('exposes a unified cup history model with season, place, result and event identity', () => {
    assert.ok(api.includes('export interface AthleteCupHistory'), 'cup history interface');
    for (const field of [
      'sourceSeasonNumber',
      'cup:',
      'event:',
      'eventName',
      'teamKey',
      'teamName',
      'place',
      'medal',
    ]) {
      assert.ok(api.includes(field), `field ${field}`);
    }
    assert.ok(api.includes('cupHistory: AthleteCupHistory[]'), 'profile carries cup history');
  });

  it('renders a cup history section alongside league history with season, place and result', () => {
    assert.ok(page.includes('Cup history'), 'cup history card');
    assert.ok(page.includes('cupHistory.length'), 'count in title');
    assert.ok(page.includes('<th scope="col" className="numeric">Season</th>'), 'season column');
    assert.ok(page.includes('Cup / Event'), 'cup/event column');
    assert.ok(page.includes('<th scope="col">Team</th>'), 'team column');
    assert.ok(page.includes('<th scope="col" className="numeric">Place</th>'), 'place column');
    assert.ok(page.includes('<th scope="col">Result</th>'), 'result column');
    assert.ok(page.includes('P{entry.place}'), 'place value');
    assert.ok(page.includes('medalBadge(entry.medal)'), 'result uses shared medal wording');
  });

  it('keeps individual and team events unambiguous in the same season', () => {
    assert.ok(page.includes('entry.eventName'), 'event name per row');
    assert.ok(page.includes('entry.teamName'), 'team name per row');
    assert.ok(page.includes("entry.event === 'Team'"), 'team rows distinguished');
    assert.ok(page.includes('· team'), 'team placement labelled as team');
    assert.ok(page.includes('· individual'), 'individual placement labelled as individual');
    assert.ok(page.includes('leg P'), 'team leg context shown');
  });

  it('shows an empty state instead of an error when there is no cup history', () => {
    assert.ok(page.includes('No Cup appearances yet.'), 'empty state copy');
    assert.ok(page.includes('cupHistory.length === 0'), 'empty branch');
  });

  it('preserves the existing league history table and statistics', () => {
    assert.ok(page.includes('Season-by-season'), 'league history card intact');
    assert.ok(page.includes('season.seasonNumber'), 'league season column intact');
    assert.ok(page.includes('P${'), 'league finish intact');
    assert.ok(page.includes('cupSelections'), 'selections section intact');
    assert.ok(page.includes('honours'), 'honours section intact');
  });
});
