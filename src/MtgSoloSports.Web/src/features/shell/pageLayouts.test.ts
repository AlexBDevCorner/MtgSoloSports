import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const srcRoot = join(here, '..', '..');

function read(rel: string): string {
  return readFileSync(join(srcRoot, rel), 'utf8');
}

const app = read('App.tsx');
const records = read('features/records/RecordsPage.tsx');
const cupsHub = read('features/cups/CupsHubPage.tsx');
const cupEdition = read('features/cups/CupEditionPage.tsx');
const cupTeam = read('features/cups/CupTeamPage.tsx');
const athlete = read('features/athletes/AthleteProfilePage.tsx');

describe('records, cups and athlete layouts', () => {
  it('records panels flow in the auto-fit grid with notes behind info', () => {
    assert.ok(records.includes('className="page-grid"'));
    assert.ok((records.match(/info=\{/g) ?? []).length >= 2);
  });

  it('cups route to a hub, an edition page and a team page', () => {
    assert.ok(app.includes('<CupsHubPage') && app.includes('<CupEditionPage') && app.includes('<CupTeamPage'));
    assert.ok(app.includes("route.view.kind === 'edition'") && app.includes("route.view.kind === 'team'"));
    assert.ok(!app.includes('ColorCupPage') && !app.includes('TypeCupPage'), 'latest-only Cup pages are gone');
    for (const [file, page] of [
      ['CupsHubPage.tsx', cupsHub],
      ['CupEditionPage.tsx', cupEdition],
      ['CupTeamPage.tsx', cupTeam],
    ]) {
      assert.ok(page.includes('className="page-grid"'), `${file} uses the grid`);
      assert.ok(page.includes('info={'), `${file} moves notes behind info`);
    }
  });

  it('athlete profile uses one grid below the hero', () => {
    assert.equal((athlete.match(/className="page-grid/g) ?? []).length, 1, 'single grid');
    assert.ok(!athlete.includes('cards-3'));
    assert.ok((athlete.match(/info=\{/g) ?? []).length >= 5);
  });
});
