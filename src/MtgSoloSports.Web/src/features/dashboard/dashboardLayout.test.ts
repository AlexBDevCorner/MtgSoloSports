import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const page = readFileSync(join(here, 'DashboardPage.tsx'), 'utf8');
const css = readFileSync(join(here, 'DashboardPage.css'), 'utf8');

describe('dashboard layout', () => {
  it('drops the navigation card that duplicated the rail', () => {
    assert.ok(!page.includes('Competitions · live event · history · records'));
    assert.ok(!page.includes('eyebrow="Navigate"'));
  });

  it('leads with a KPI strip and a single next-action panel', () => {
    assert.ok(page.includes('className="kpis"'), 'KPI strip renders');
    assert.ok(page.includes('title="Next step"'), 'next-action panel is titled plainly');
    assert.ok(page.includes('<SeasonFlow'), 'season flow drives the primary action');
    assert.ok(page.includes('Status details'), 'lifecycle rows collapse');
    assert.ok(page.includes('Roster details'), 'pool/checksum collapse');
  });

  it('keeps contextual links and moves system notes behind info', () => {
    assert.ok(page.includes('standingsPath(saveId)'), 'stage gate links to standings');
    assert.ok((page.match(/info=\{/g) ?? []).length >= 5, 'system notes live in info disclosures');
    assert.ok(page.includes("import './DashboardPage.css'"), 'feature stylesheet is loaded');
  });

  it('never forces horizontal scroll on phones', () => {
    assert.ok(css.includes('minmax(min(320px, 100%), 1fr)'));
  });
});
