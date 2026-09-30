import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { catalogStatus } from './catalogStatus.ts';

const sufficient = { totalAthletes: 2048, countsBySportingColor: {}, isSufficientForSave: true };
const partial = { totalAthletes: 900, countsBySportingColor: {}, isSufficientForSave: false };

describe('rail catalog status', () => {
  it('is dim while the first load is in flight', () => {
    assert.deepEqual(catalogStatus(null, true), { tone: 'dim', label: 'Checking catalog…' });
  });

  it('warns when there is no catalog', () => {
    assert.deepEqual(catalogStatus(null, false), { tone: 'warn', label: 'No card catalog' });
    assert.deepEqual(
      catalogStatus({ totalAthletes: 0, countsBySportingColor: {}, isSufficientForSave: false }, false),
      { tone: 'warn', label: 'No card catalog' },
    );
  });

  it('warns with the count when the catalog is below quota', () => {
    assert.deepEqual(catalogStatus(partial, false), { tone: 'warn', label: 'Catalog 900 · incomplete' });
  });

  it('is ok with the count when every color is at quota', () => {
    assert.deepEqual(catalogStatus(sufficient, false), { tone: 'ok', label: 'Catalog 2048' });
  });

  it('keeps the last known status while a refresh is in flight', () => {
    assert.deepEqual(catalogStatus(sufficient, true), { tone: 'ok', label: 'Catalog 2048' });
    assert.deepEqual(catalogStatus(partial, true), { tone: 'warn', label: 'Catalog 900 · incomplete' });
  });
});
