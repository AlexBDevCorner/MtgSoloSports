import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const panel = readFileSync(join(here, 'ScryfallImportPanel.tsx'), 'utf8');

describe('scryfall import panel layout', () => {
  it('collapses the long import explanation instead of repeating it inline', () => {
    const details = panel.indexOf('<details className="advanced">');
    const summary = panel.indexOf('<summary>How catalog import works</summary>');
    const explanation = panel.indexOf('We keep eligible creature cards');
    assert.ok(details >= 0 && summary > details, 'explanation sits behind a disclosure');
    assert.ok(explanation > summary, 'explanation text is inside the disclosure');
  });

  it('keeps Scryfall attribution visible', () => {
    const summary = panel.indexOf('<summary>How catalog import works</summary>');
    const attribution = panel.indexOf('Card data and images courtesy of Scryfall');
    assert.ok(attribution >= 0 && attribution < summary, 'attribution renders before the disclosure');
  });
});
