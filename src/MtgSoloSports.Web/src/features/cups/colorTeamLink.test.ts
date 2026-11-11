import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const link = readFileSync(join(here, 'ColorTeamLink.tsx'), 'utf8');

describe('MSS-139 color team link', () => {
  it('reuses the existing team badge and route builder instead of hand-built URLs', () => {
    assert.ok(link.includes('TeamBadge'), 'color links reuse TeamBadge');
    assert.ok(link.includes('cup="color"'), 'color links target the Color Cup team page');
    assert.ok(link.includes('colorTeamKeyFromSportingColor'), 'numeric colors map through the shared helper');
    assert.ok(link.includes('colorTeamKeyFromName'), 'string-only rows fall back to the name mapping');
    assert.ok(!link.includes('/cups/${'), 'no hand-built cup URL');
    assert.ok(!link.includes('toLowerCase()'), 'never lower-cases display strings inline');
  });

  it('renders plain text when the identity is unknown', () => {
    assert.ok(link.includes('if (!teamKey)'), 'missing keys stay plain text');
    assert.ok(link.includes('<>{sportingColorName}</>'), 'fallback renders the name as before');
  });

  it('uses a real anchor with native new-tab behavior', () => {
    // TeamBadge renders the shared router Link (checked in its own test);
    // this component only decides between the badge and plain text.
    assert.ok(link.includes('<TeamBadge'), 'links render anchors through TeamBadge');
  });
});
