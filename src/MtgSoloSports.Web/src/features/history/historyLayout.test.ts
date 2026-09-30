import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const page = readFileSync(join(here, 'HistoryPage.tsx'), 'utf8');

describe('history layout', () => {
  it('replaces the navigation card with a sticky toolbar', () => {
    assert.ok(page.includes('className="toolbar"'));
    assert.ok(!page.includes('eyebrow="History navigation"'));
  });

  it('keeps the replay full width ahead of the panel grid', () => {
    const replay = page.indexOf('<RoundReveal');
    const grid = page.indexOf('className="page-grid"');
    assert.ok(replay > 0 && grid > replay, 'replay renders before the grid');
    assert.ok(page.indexOf('eyebrow="Post-season Cup"') > grid, 'cup panel lives in the grid');
  });

  it('moves cup explanations behind info and links to Cups from the header', () => {
    assert.ok(page.includes('Open Cups'));
    assert.ok(!page.includes('Open the <Link'));
  });
});
