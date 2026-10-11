import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const srcRoot = join(here, '..', '..');
const read = (rel: string): string => readFileSync(join(srcRoot, rel), 'utf8');

const page = read('features/athletes/AthleteProfilePage.tsx');
const summary = read('features/athletes/athleteCareerSummary.ts');
const baseCss = read('shared/ui/base.css');

describe('MSS-076 athlete profile at-a-glance summary', () => {
  it('derives the summary from persisted profile data without resimulating', () => {
    assert.ok(page.includes("from './athleteCareerSummary'"), 'profile uses the career summary helper');
    assert.ok(page.includes('summarizeCareer(seasons, cupSelections, cupHistory)'), 'summary covers seasons, selections and history');
    assert.ok(summary.includes('tierForSeason'), 'tier resolution is centralized');
    assert.ok(summary.includes('summarizeSeasonTiers'), 'season totals are centralized');
    assert.ok(summary.includes('summarizeTitleTiers'), 'title totals are centralized');
    assert.ok(summary.includes('summarizeCaps'), 'caps are centralized');
    assert.ok(summary.includes('summarizeMedals'), 'medals are centralized');
  });

  it('keeps relevant identity and removes MTG mana cost and set metadata', () => {
    // SetCode is the representative printing set code (BulkCatalogParser
    // copies card.Set; SaveAthleteEntity stores the printing set code for
    // artwork reference), so it stays out of this sports pane.
    assert.ok(!page.includes('manaCost'), 'mana cost is absent from the profile pane');
    assert.ok(!page.includes('setCode'), 'printing set code is absent from the profile pane');
    assert.ok(page.includes('sportingColorName'), 'sporting color is shown');
    assert.ok(page.includes('athlete-identity-item--color'), 'color is prominent, not tiny faint text');
    assert.ok(page.includes('creatureTypes'), 'creature types are shown');
    assert.ok(page.includes('Type Cup nationality'), 'nationality label is recognizable');
    assert.ok(page.includes('Current league'), 'current league label is recognizable');
    assert.ok(page.includes('Status'), 'status label is recognizable');
    assert.ok(page.includes('currentLeagueText'), 'absent leagues render honestly instead of invented');
  });

  it('shows five distinct numeric season totals by tier', () => {
    assert.ok(page.includes('aria-label="Seasons by tier"'), 'seasons group is labeled');
    for (const label of ['Superleague', 'Feeder 1', 'Feeder 2', 'Feeder 3', 'Pool']) {
      assert.ok(page.includes(`<span className="metric-label">${label}</span>`), `season cell: ${label}`);
    }
    assert.ok(page.includes('summary.seasons.superleague'), 'superleague seasons value');
    assert.ok(page.includes('summary.seasons.feeder1'), 'feeder 1 seasons value');
    assert.ok(page.includes('summary.seasons.feeder2'), 'feeder 2 seasons value');
    assert.ok(page.includes('summary.seasons.feeder3'), 'feeder 3 seasons value');
    assert.ok(page.includes('summary.seasons.pool'), 'pool seasons value');
  });

  it('shows four distinct numeric league title totals by tier', () => {
    assert.ok(page.includes('aria-label="Titles by tier"'), 'titles group is labeled');
    assert.ok(page.includes('summary.titles.superleague'), 'superleague titles value');
    assert.ok(page.includes('summary.titles.feeder1'), 'feeder 1 titles value');
    assert.ok(page.includes('summary.titles.feeder2'), 'feeder 2 titles value');
    assert.ok(page.includes('summary.titles.feeder3'), 'feeder 3 titles value');
  });

  it('summarizes caps, medals and the current active bonus', () => {
    assert.ok(page.includes('aria-label="Cup caps and medals"'), 'cups group is labeled');
    assert.ok(page.includes('Color caps'), 'color caps label');
    assert.ok(page.includes('Type caps'), 'type caps label');
    assert.ok(page.includes('summary.caps.color'), 'color caps value');
    assert.ok(page.includes('summary.caps.type'), 'type caps value');
    for (const label of ['Gold', 'Silver', 'Bronze']) {
      assert.ok(page.includes(`<span className="metric-label">${label}</span>`), `medal cell: ${label}`);
    }
    assert.ok(page.includes('summary.medals.gold'), 'gold value');
    assert.ok(page.includes('summary.medals.silver'), 'silver value');
    assert.ok(page.includes('summary.medals.bronze'), 'bronze value');
    assert.ok(page.includes('aria-label="Current active bonus"'), 'bonus group is labeled');
    assert.ok(page.includes('formatBonus(career.currentEffectiveBonusThousandths)'), 'bonus uses the current effective value with sign/unit');
    assert.ok(page.includes('No active bonus'), 'clear zero state for the bonus');
  });

  it('uses readable grouped metric cells instead of a miniature data dump', () => {
    assert.ok(page.includes('athlete-summary'), 'summary container');
    assert.ok(page.includes('athlete-metric-cells'), 'grouped metric cells');
    assert.ok(page.includes('metric-value'), 'prominent numeric values');
    assert.ok(baseCss.includes('.athlete-metric-cells .metric-value'), 'metric value styling exists');
    assert.match(baseCss, /\.athlete-metric-cells \.metric-value\s*\{[^}]*font-size:\s*18px/);
    assert.match(baseCss, /\.athlete-metric-cells \.metric-value\s*\{[^}]*color:\s*var\(--text\)/);
    assert.match(baseCss, /\.athlete-bonus-value\s*\{[^}]*font-size:\s*20px/);
  });

  it('reflows within the pane without clipping or page-level horizontal overflow', () => {
    assert.ok(baseCss.includes('.athlete-summary'), 'summary styling exists');
    assert.ok(baseCss.includes('.athlete-metrics'), 'metrics grid styling exists');
    assert.match(baseCss, /\.athlete-metrics\s*\{[^}]*grid-template-columns:\s*repeat\(auto-fit,\s*minmax\(/);
    assert.match(baseCss, /\.athlete-metrics\s*\{[^}]*min-width:\s*0/);
    assert.match(baseCss, /\.athlete-metric-cells\s*\{[^}]*grid-template-columns:\s*repeat\(auto-fit,\s*minmax\(/);
    assert.match(baseCss, /\.athlete-identity\s*\{[^}]*flex-wrap:\s*wrap/);
    assert.ok(baseCss.includes('.athlete-header .card-art-large'), 'artwork sizing is preserved');
  });

  it('preserves artwork, name, navigation and all other athlete panes', () => {
    assert.ok(page.includes('card-art-large'), 'card artwork stays');
    assert.ok(page.includes('card.name'), 'athlete name stays');
    assert.ok(page.includes('Back to live'), 'live navigation stays');
    assert.ok(page.includes('Dashboard'), 'dashboard navigation stays');
    for (const eyebrow of [
      'eyebrow="Career"',
      'eyebrow="Bonus"',
      'eyebrow="Identity"',
      'eyebrow="Honours"',
      'eyebrow="Movements"',
      'eyebrow="Selections"',
      'eyebrow="Records"',
      'eyebrow="History"',
      'eyebrow="Cups"',
      'eyebrow="Stories"',
    ]) {
      assert.ok(page.includes(eyebrow), `pane preserved: ${eyebrow}`);
    }
  });
});
