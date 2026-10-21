import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const srcRoot = join(here, '..', '..');
const read = (rel: string): string => readFileSync(join(srcRoot, rel), 'utf8');

const page = read('features/records/RecordsPage.tsx');
const api = read('features/records/recordsApi.ts');
const dashboard = read('features/dashboard/DashboardPage.tsx');

describe('MSS-083 career performance ranking', () => {
  it('orders Career Leaders by career points, not titles or bonus', () => {
    assert.ok(page.includes('Career points'), 'points column is shown');
    assert.ok(
      page.includes('career performance points, then major-event wins'),
      'ordering rationale is documented in the UI',
    );
    assert.ok(!page.includes('Ordered by total titles'), 'old title ordering copy is gone');
  });

  it('shows the selected period and period-scoped titles', () => {
    assert.ok(page.includes('periodLabel'), 'heading names the selected window');
    assert.ok(page.includes('All time'), 'all-time default exists');
    assert.ok(page.includes('Single season'), 'single-season window exists');
    assert.ok(page.includes('Custom range'), 'custom range window exists');
    assert.ok(page.includes('row.totalTitles'), 'titles remain as supporting statistics');
  });

  it('makes the score auditable with a per-athlete breakdown', () => {
    assert.ok(page.includes('LeaderBreakdown'), 'breakdown component exists');
    assert.ok(page.includes('Where points came from'), 'breakdown is discoverable');
    assert.ok(page.includes('row.superleaguePoints'), 'league tier split is shown');
    assert.ok(page.includes('row.colorCupIndividualPoints'), 'individual cup split is shown');
    assert.ok(page.includes('row.colorCupTeamPoints'), 'team cup split is shown');
    assert.ok(page.includes('row.typeCupTeamPoints'), 'type cup split is shown');
  });

  it('does not present active bonus as part of the score', () => {
    assert.ok(!page.includes('Bonus</th>'), 'bonus column is removed from Career Leaders');
    assert.ok(!page.includes('formatBonus'), 'bonus formatting is gone from the leaders view');
  });

  it('recomputes from finalized results per window and validates the range', () => {
    assert.ok(api.includes('fromSeason'), 'API sends the from season');
    assert.ok(api.includes('toSeason'), 'API sends the to season');
    assert.ok(page.includes('fromSeason'), 'range hookup passes the from season');
    assert.ok(page.includes('Invalid season range'), 'from<=to is validated in the UI');
    assert.ok(
      page.includes('No leaders yet for this window'),
      'empty windows render an empty state',
    );
  });

  it('preserves the window across navigation and resets on universe change', () => {
    assert.ok(
      page.includes('hall-of-fame-period'),
      'the selected window persists per universe for back navigation',
    );
    assert.ok(page.includes('saveId'), 'persistence is keyed by universe');
  });

  it('stays readable on narrow widths', () => {
    assert.ok(page.includes('table-wrap'), 'the table scrolls instead of overflowing');
  });

  it('updates the dashboard summary to points instead of bonus', () => {
    assert.ok(dashboard.includes('Career points'), 'dashboard shows points');
    assert.ok(!dashboard.includes('formatBonus'), 'dashboard no longer shows bonus in the summary');
  });

  it('requests windows through the hall-of-fame API', () => {
    assert.ok(api.includes('fromSeason'), 'period reaches the backend');
    assert.ok(api.includes('careerPoints'), 'leader shape carries points');
    assert.ok(api.includes('weightsVersion'), 'leader shape carries the weights version');
  });
});
