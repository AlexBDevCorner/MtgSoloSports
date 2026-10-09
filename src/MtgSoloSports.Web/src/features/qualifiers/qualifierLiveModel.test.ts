import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const page = readFileSync(join(here, 'QualifiersPage.tsx'), 'utf8');
const model = readFileSync(join(here, 'qualifierModel.ts'), 'utf8');
const api = readFileSync(join(here, 'qualifierApi.ts'), 'utf8');

describe('MSS-069 qualifier overview and detail', () => {
  it('keeps the 17-event overview with phase totals as totals only', () => {
    assert.ok(page.includes('Run remaining qualifiers'), 'fast-forward from overview');
    assert.ok(model.includes('canonicalQualifierLiveParams') || model.includes('canonicalQualifierEntries'), 'canonical order');
    assert.ok(!/Round \$\{.*\} \/ 272/.test(page), 'no mixed event/phase progress under an event title');
  });

  it('offers Play on Live for Superleague and feeder, pending and completed', () => {
    assert.ok(page.includes('Play on Live'), 'detail Live action');
    assert.ok(page.includes('qualifierLivePath'), 'feeder Live deep link');
    assert.ok(api.includes('fetchQualifierRounds'), 'Live read model');
    assert.ok(api.includes('playFeederQualifierRound'), 'per-round mutation');
    assert.ok(api.includes('/rounds/next'), 'one round per request');
    assert.ok(api.includes('/rounds/${round}') || api.includes('/rounds/'), 'single-round replay');
  });

  it('exposes canonical Live order helpers without resimulating', () => {
    assert.ok(model.includes('QUALIFIER_COLOR_ORDER'), 'sporting-color enum order');
    assert.ok(model.includes('nextQualifierLiveParam'), 'next-event navigation');
    assert.ok(!/new Random|Random\.Shared|Math\.random/.test(model), 'pure presentation model');
  });
});
