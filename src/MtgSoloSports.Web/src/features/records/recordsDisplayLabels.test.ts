import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { careerRecordLabel, scoringRecordLabel, scoringScopeLabel } from '../../shared/displayLabels.ts';

const here = dirname(fileURLToPath(import.meta.url));
const srcRoot = join(here, '..', '..');
const read = (rel: string): string => readFileSync(join(srcRoot, rel), 'utf8');

const page = read('features/records/RecordsPage.tsx');

describe('MSS-072 records display labels', () => {
  it('routes every record, honour and history label through shared helpers', () => {
    assert.ok(page.includes('careerRecordLabel(record.recordKey, record.label)'), 'career records use the shared helper');
    assert.ok(page.includes('scoringRecordLabel(record.recordKey, record.label)'), 'scoring labels use the shared helper');
    assert.ok(page.includes('scoringScopeLabel(record.scope)'), 'scoring scopes use the shared helper');
    assert.ok(page.includes('careerRecordLabel(item.recordKey)'), 'record-break history uses the shared helper');
    assert.ok(page.includes('honourKindLabel(row.honourKind)'), 'honours use the shared helper');
  });

  it('keeps one Color spelling across scoring copy', () => {
    assert.ok(!page.includes('Colour Cup'), 'British spelling is gone from records copy');
    assert.ok(page.includes('Color Cup'), 'American spelling is used');
    assert.equal(
      scoringRecordLabel('x', 'Most athlete points in single Colour Cup individual round'),
      'Most athlete points in single Color Cup individual round',
    );
    assert.equal(scoringScopeLabel('Colour Cup team'), 'Color Cup team');
  });

  it('renders historic record keys in readable English', () => {
    assert.equal(careerRecordLabel('superleague_titles', 'Superleague titles'), 'Superleague titles');
    assert.equal(careerRecordLabel('longest_superleague_tenure', 'Superleague tenure'), 'Longest Superleague tenure');
    assert.equal(careerRecordLabel('highest_effective_bonus', 'effective bonus'), 'Highest effective bonus');
  });
});
