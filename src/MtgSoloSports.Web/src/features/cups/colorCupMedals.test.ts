import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  combineMedalRows,
  medalTotal,
  medalViewDescription,
  medalViewHeading,
  medalViewLabel,
  sortMedalRows,
  toMedalRows,
} from './colorCupMedals.ts';

const here = dirname(fileURLToPath(import.meta.url));
const read = (file: string) => readFileSync(join(here, file), 'utf8');

describe('MSS-081 color cup medals model', () => {
  it('totals gold plus silver plus bronze', () => {
    assert.equal(medalTotal({ gold: 1, silver: 2, bronze: 3 }), 6);
    assert.equal(medalTotal({ gold: 0, silver: 0, bronze: 0 }), 0);
  });

  it('sorts by gold, then silver, then bronze, then team name like the team table', () => {
    const rows = sortMedalRows([
      { teamKey: 'red', teamName: 'Red', gold: 0, silver: 1, bronze: 0 },
      { teamKey: 'white', teamName: 'White', gold: 1, silver: 0, bronze: 0 },
      { teamKey: 'blue', teamName: 'Blue', gold: 1, silver: 0, bronze: 0 },
      { teamKey: 'black', teamName: 'Black', gold: 0, silver: 0, bronze: 1 },
    ]);
    assert.deepEqual(
      rows.map((row) => row.teamKey),
      ['blue', 'white', 'red', 'black'],
    );
  });

  it('combines team and individual exactly per color and medal', () => {
    const combined = combineMedalRows(
      [
        { teamKey: 'white', teamName: 'White', gold: 1, silver: 0, bronze: 0 },
        { teamKey: 'blue', teamName: 'Blue', gold: 0, silver: 1, bronze: 0 },
      ],
      [
        { teamKey: 'white', teamName: 'White', gold: 1, silver: 0, bronze: 1 },
        { teamKey: 'red', teamName: 'Red', gold: 0, silver: 0, bronze: 1 },
      ],
    );
    const byKey = new Map(combined.map((row) => [row.teamKey, row]));
    // One team gold plus one individual gold is two combined gold for white.
    assert.equal(byKey.get('white')?.gold, 2);
    assert.equal(byKey.get('white')?.bronze, 1);
    assert.equal(byKey.get('blue')?.silver, 1);
    assert.equal(byKey.get('red')?.bronze, 1);
    // Combined stays sorted like the team table.
    assert.deepEqual(
      combined.map((row) => [row.gold, row.silver, row.bronze]),
      [...combined.map((row) => [row.gold, row.silver, row.bronze])].sort((a, b) => b[0] - a[0] || b[1] - a[1] || b[2] - a[2]),
    );
  });

  it('keeps team rows as one medal per podium, not per squad member', () => {
    const teams = toMedalRows([
      { teamKey: 'white', teamName: 'White', editions: 2, gold: 1, silver: 0, bronze: 0, bestRank: 1, lastSeasonNumber: 3 },
    ]);
    const combined = combineMedalRows(teams, [
      { teamKey: 'white', teamName: 'White', gold: 1, silver: 0, bronze: 0 },
    ]);
    assert.equal(combined[0]?.gold, 2);
  });

  it('labels every view scope explicitly', () => {
    assert.equal(medalViewLabel('team'), 'Team');
    assert.equal(medalViewLabel('individual'), 'Individual');
    assert.equal(medalViewLabel('combined'), 'Combined');
    assert.ok(medalViewHeading('team').includes('Team'));
    assert.ok(medalViewHeading('individual').includes('Individual'));
    assert.ok(medalViewHeading('combined').includes('Combined'));
    assert.ok(medalViewDescription('team').includes('one medal per Color Cup team podium'));
    assert.ok(medalViewDescription('individual').includes('represented in that edition'));
    assert.ok(medalViewDescription('combined').includes('team medals plus individual medals'));
  });
});

describe('MSS-081 cups hub medal switcher', () => {
  const hub = read('CupsHubPage.tsx');
  const api = read('cupHistoryApi.ts');

  it('offers an obvious Team, Individual and Combined switcher reusing the table styling', () => {
    assert.ok(hub.includes('role="tablist"'), 'switcher is grouped');
    assert.ok(hub.includes("aria-label=\"Color Cup medal standings view\""), 'switcher is labeled');
    assert.ok(hub.includes('COLOR_MEDAL_VIEWS'), 'all three views from the shared helper');
    assert.ok(hub.includes('ColorMedalTables'), 'dedicated color medal tables');
    assert.ok(hub.includes('medalViewHeading'), 'heading names the scope');
    assert.ok(hub.includes('medalViewDescription'), 'scope is described');
    assert.ok(hub.includes('className="table-wrap"'), 'reuses the table wrap');
    assert.ok(hub.includes('className="data-table"'), 'reuses the data table');
    assert.ok(hub.includes('primary-button') && hub.includes('ghost-button'), 'reuses button styling');
  });

  it('shows color, gold, silver, bronze and total with the team badge', () => {
    assert.ok(hub.includes('<TeamBadge'), 'colors link to team pages');
    assert.ok(hub.includes('🥇') && hub.includes('🥈') && hub.includes('🥉'), 'medal columns');
    assert.ok(hub.includes('Total'), 'total column');
    assert.ok(hub.includes('medalTotal('), 'total is gold plus silver plus bronze');
  });

  it('keeps the existing team columns and explains the one-medal rule', () => {
    assert.ok(hub.includes('Cups') && hub.includes('Best') && hub.includes('Last'), 'team view keeps Cups/Best/Last');
    assert.ok(hub.includes('not multiplied by squad size'), 'roster-size rule is explicit');
    assert.ok(hub.includes('each individual podium counts'), 'individual rule is explicit');
  });

  it('has meaningful empty states and never invents medals', () => {
    assert.ok(hub.includes('No Color Cup has been played yet.'), 'new-universe empty state');
    assert.ok(hub.includes('No individual Color Cup medals have been awarded yet.'), 'unfinished-cup empty state');
    assert.ok(hub.includes('No Color Cup medals have been awarded yet.'), 'combined empty state');
  });

  it('reads the additive backend tables with a backward-compatible fallback', () => {
    assert.ok(api.includes('colorIndividualMedals?'), 'individual medals are additive/optional');
    assert.ok(api.includes('colorCombinedMedals?'), 'combined medals are additive/optional');
    assert.ok(hub.includes('colorIndividualMedals ?? []'), 'missing tables fall back to empty');
    assert.ok(hub.includes('combineMedalRows('), 'combined falls back to team plus individual');
    assert.ok(hub.includes('sortMedalRows('), 'ordering matches the team table');
  });
});
