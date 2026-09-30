import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const page = readFileSync(join(here, 'SavesPage.tsx'), 'utf8');
const css = readFileSync(join(here, 'SavesPage.css'), 'utf8');

describe('saves layout', () => {
  it('puts the save list first with management forms in a side column', () => {
    assert.ok(page.includes('className="saves-layout"'));
    assert.ok(page.includes('className="saves-side"'));
    assert.ok(page.indexOf('Select a universe') < page.indexOf('saves-side'), 'list comes first');
  });

  it('orders side panels by frequency of use', () => {
    const create = page.indexOf('Create a universe');
    const importSave = page.indexOf('Import a save');
    const catalog = page.indexOf('Refresh from Scryfall');
    assert.ok(create < importSave && importSave < catalog);
  });

  it('collapses to one column on narrow screens', () => {
    assert.match(css, /@media \(max-width: 1020px\)[\s\S]*\.saves-layout\s*\{[^}]*minmax\(0, 1fr\)/);
    assert.ok(page.includes("import './SavesPage.css'"));
  });
});
