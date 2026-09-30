import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const page = readFileSync(join(here, 'StandingsPage.tsx'), 'utf8');

describe('standings toolbar', () => {
  it('replaces the navigation card with a sticky toolbar', () => {
    assert.ok(page.includes('className="toolbar"'));
    assert.ok(!page.includes('eyebrow="Standings navigation"'));
  });

  it('uses one segmented control for the view', () => {
    assert.ok(page.includes('className="segmented"'));
    assert.ok(page.includes('aria-label="Table views"'));
    assert.ok(!page.includes('<option value="matrix">'), 'duplicate view select is gone');
  });

  it('keeps the URL note available behind info', () => {
    assert.ok(page.includes('<InfoDisclosure>'));
  });
});
