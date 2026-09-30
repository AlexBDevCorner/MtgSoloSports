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
const colorCup = read('features/cups/ColorCupPage.tsx');
const typeCup = read('features/cups/TypeCupPage.tsx');
const athlete = read('features/athletes/AthleteProfilePage.tsx');

describe('records, cups and athlete layouts', () => {
  it('records panels flow in the auto-fit grid with notes behind info', () => {
    assert.ok(records.includes('className="page-grid"'));
    assert.ok((records.match(/info=\{/g) ?? []).length >= 2);
  });

  it('cups render as titled sections with grids', () => {
    assert.ok(app.includes('className="page-section"'));
    assert.ok(app.includes('Color Cup') && app.includes('Type Cup'));
    for (const [file, page] of [
      ['ColorCupPage.tsx', colorCup],
      ['TypeCupPage.tsx', typeCup],
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
