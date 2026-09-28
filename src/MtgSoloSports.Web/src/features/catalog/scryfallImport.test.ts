import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { ApiError } from '../../shared/api/http.ts';
import {
  countForColor,
  findShortfalls,
  formatSourceLabel,
  needsRefreshConfirmation,
  tryParseQuotaError,
} from './scryfallImportApi.ts';

describe('scryfall import ui states', () => {
  it('requires confirmation only when a catalog already exists', () => {
    assert.equal(needsRefreshConfirmation(0), false);
    assert.equal(needsRefreshConfirmation(1), true);
    assert.equal(needsRefreshConfirmation(2048), true);
  });

  it('reads counts regardless of key casing', () => {
    assert.equal(countForColor({ White: 10 }, 'White'), 10);
    assert.equal(countForColor({ white: 11 }, 'White'), 11);
    assert.equal(countForColor({ WHITE: 12 }, 'White'), 12);
    assert.equal(countForColor({}, 'White'), 0);
  });

  it('finds per-color shortfalls against the 256 quota', () => {
    const counts: Record<string, number> = {
      White: 256,
      Blue: 255,
      Black: 300,
      Red: 0,
      Green: 256,
      Multicolor: 256,
      Hybrid: 256,
      Colorless: 256,
    };
    assert.deepEqual(findShortfalls(counts), ['Blue', 'Red']);
    assert.deepEqual(
      findShortfalls({
        White: 256,
        Blue: 256,
        Black: 256,
        Red: 256,
        Green: 256,
        Multicolor: 256,
        Hybrid: 256,
        Colorless: 256,
      }),
      [],
    );
  });

  it('never fabricates freshness when metadata is missing', () => {
    assert.equal(formatSourceLabel('Default Cards', '2026-09-28T09:05:39Z'), 'Source: Default Cards (updated 2026-09-28T09:05:39Z)');
    assert.equal(formatSourceLabel('Default Cards', null), 'Source: Default Cards');
    assert.equal(formatSourceLabel(null, null), null);
    assert.equal(formatSourceLabel('  ', '  '), null);
  });

  it('parses quota-inadequate errors without claiming success', () => {
    const body = JSON.stringify({
      error: 'The Scryfall dataset cannot supply a save universe: Red has 1, needs 256. The existing catalog was left unchanged.',
      countsBySportingColor: { White: 256, Red: 1 },
      insufficient: ['Red'],
      sourceName: 'Default Cards',
      sourceUpdatedAt: '2026-09-28T09:05:39Z',
    });
    const parsed = tryParseQuotaError(new ApiError(422, body));
    assert.ok(parsed);
    assert.equal(parsed!.insufficient.join(','), 'Red');
    assert.equal(parsed!.countsBySportingColor['Red'], 1);
  });

  it('ignores non-quota errors for the shortfall view', () => {
    assert.equal(tryParseQuotaError(new ApiError(502, '{"error":"down"}')), null);
    assert.equal(tryParseQuotaError(new Error('boom')), null);
  });
});
