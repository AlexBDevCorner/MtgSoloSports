import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const srcRoot = join(here, '..', '..');
const read = (rel: string): string => readFileSync(join(srcRoot, rel), 'utf8');

const page = read('features/athletes/AthleteProfilePage.tsx');
const baseCss = read('shared/ui/base.css');

function countOccurrences(source: string, needle: string): number {
  return source.split(needle).length - 1;
}

describe('MSS-082 athlete top region: Profile beside History', () => {
  it('places one Profile summary and one History panel together at the top', () => {
    assert.ok(page.includes('className="athlete-top"'), 'leading top region exists');
    assert.ok(page.includes('className="athlete-header"'), 'profile keeps its compact header treatment');
    assert.equal(countOccurrences(page, 'eyebrow="History"'), 1, 'exactly one History panel, moved not duplicated');
    const topAt = page.indexOf('className="athlete-top"');
    const profileAt = page.indexOf('className="athlete-header"');
    const historyAt = page.indexOf('eyebrow="History"');
    const gridAt = page.indexOf('page-grid page-grid--panels');
    assert.ok(topAt >= 0 && profileAt > topAt, 'profile lives inside the top region');
    assert.ok(historyAt > profileAt, 'History follows Profile in DOM/tab order');
    assert.ok(gridAt > historyAt, 'remaining panels flow beneath the top region');
  });

  it('keeps History fully functional with its existing columns and interactions', () => {
    const historyAt = page.indexOf('eyebrow="History"');
    assert.ok(historyAt > 0, 'History renders');
    assert.ok(page.slice(historyAt, historyAt + 400).includes('span-wide'), 'History keeps its wide span');
    assert.ok(page.slice(historyAt, historyAt + 400).includes('athlete-history'), 'History opts into the top-region treatment');
    for (const label of ['Season', 'Status', 'Finish', 'Round W', 'Stage W', '2nd/3rd', 'Earned', 'Champ pts']) {
      assert.ok(page.includes(label), `History column preserved: ${label}`);
    }
    assert.ok(page.includes('seasons.map((season)'), 'season ordering renders from the same payload');
    assert.ok(page.includes('leagueLevelLabel'), 'status keeps tier/division detail');
    assert.ok(page.includes('formatBonus(season.earnedBonusThousandths)'), 'earned bonuses preserved');
    assert.ok(page.includes('formatPoints(season.totalChampionshipPointsThousandths)'), 'championship points preserved');
  });

  it('keeps the profile summary legible with artwork, metrics, bonus and navigation', () => {
    assert.ok(page.includes('card-art-large'), 'card artwork stays');
    assert.ok(page.includes('card.name'), 'athlete name stays');
    assert.ok(page.includes('Back to live'), 'live navigation stays');
    assert.ok(page.includes('Dashboard'), 'dashboard navigation stays');
    assert.ok(page.includes('athlete-summary'), 'summary container stays');
    assert.ok(page.includes('athlete-metric-cells'), 'grouped metric cells stay');
    assert.ok(page.includes('aria-label="Seasons by tier"'), 'season totals stay');
    assert.ok(page.includes('aria-label="Titles by tier"'), 'title totals stay');
    assert.ok(page.includes('aria-label="Cup caps and medals"'), 'cup totals stay');
    assert.ok(page.includes('aria-label="Current active bonus"'), 'active bonus stays');
  });

  it('reflows the remaining panels beneath without losing content', () => {
    for (const eyebrow of [
      'eyebrow="Career"',
      'eyebrow="Bonus"',
      'eyebrow="Identity"',
      'eyebrow="Honours"',
      'eyebrow="Movements"',
      'eyebrow="Selections"',
      'eyebrow="Records"',
      'eyebrow="Cups"',
      'eyebrow="Stories"',
    ]) {
      assert.ok(page.includes(eyebrow), `panel preserved below the top region: ${eyebrow}`);
    }
    const gridAt = page.indexOf('page-grid page-grid--panels');
    const gridSection = page.slice(gridAt);
    assert.ok(!gridSection.includes('eyebrow="History"'), 'no duplicate History farther down');
    const cupsAt = page.indexOf('eyebrow="Cups"');
    assert.ok(page.slice(cupsAt, cupsAt + 400).includes('span-wide'), 'Cups keeps its wider-panel treatment');
  });

  it('lays Profile and History side by side on desktop without page overflow', () => {
    assert.match(baseCss, /\.athlete-top\s*\{[^}]*display:\s*grid/);
    assert.match(baseCss, /\.athlete-top\s*\{[^}]*grid-template-columns:\s*repeat\(3,\s*minmax\(0,\s*1fr\)\)/);
    assert.match(baseCss, /\.athlete-top\s*\{[^}]*min-width:\s*0/);
    assert.match(baseCss, /\.athlete-top > \.athlete-header\s*\{[^}]*grid-column:\s*span 1/);
    assert.match(baseCss, /\.athlete-top > \.athlete-history\s*\{[^}]*grid-column:\s*span 2/);
    assert.match(baseCss, /\.athlete-top > \.athlete-header\s*\{[^}]*width:\s*100%/);
    assert.match(baseCss, /\.athlete-top > \.athlete-header\s*\{[^}]*align-self:\s*start/);
  });

  it('bounds top History with independent vertical scrolling like established panels', () => {
    assert.match(baseCss, /\.athlete-top > \.athlete-history\s*\{[^}]*min-height:\s*var\(--card-height\)/);
    assert.match(baseCss, /\.athlete-top > \.athlete-history\s*\{[^}]*overflow:\s*hidden/);
    assert.match(baseCss, /\.athlete-top > \.athlete-history > \.card-body\s*\{[^}]*overflow-y:\s*auto/);
    assert.match(baseCss, /\.athlete-top > \.athlete-history > \.card-body\s*\{[^}]*min-height:\s*0/);
    assert.match(baseCss, /\.athlete-top > \.athlete-history > \.card-head\s*\{[^}]*flex:\s*0 0 auto/);
    // MSS-082 correction: desktop History needs a definite height so long
    // careers scroll inside the panel instead of stretching the grid row.
    // A percentage height against the auto-sized .athlete-top row resolves
    // to content height (no cap); scope the check to the desktop rules
    // before the narrow stacking media query.
    const desktopCss = baseCss.split('@media (max-width: 980px)')[0];
    assert.match(
      desktopCss,
      /\.athlete-top > \.athlete-history\s*\{[^}]*height:\s*var\(--card-height\)/,
    );
    assert.match(
      desktopCss,
      /\.athlete-top > \.athlete-history\s*\{[^}]*max-height:\s*var\(--card-height\)/,
    );
    assert.ok(
      !/\.athlete-top > \.athlete-history\s*\{[^}]*height:\s*100%/.test(desktopCss),
      'desktop History must not use an uncapped percentage height',
    );
  });

  it('keeps the top History table readable without hiding columns', () => {
    assert.match(baseCss, /\.athlete-top > \.athlete-history \.data-table\s*\{[^}]*width:\s*100%/);
    assert.match(baseCss, /\.athlete-top > \.athlete-history \.data-table td\.numeric/);
    assert.match(baseCss, /\.athlete-top > \.athlete-history \.card-sub\s*\{[^}]*white-space:\s*normal/);
  });

  it('stacks Profile above History on narrow viewports before the other panels', () => {
    assert.match(
      baseCss,
      /@media \(max-width: 980px\)[\s\S]*?\.athlete-top\s*\{[^}]*grid-template-columns:\s*minmax\(0,\s*1fr\)/,
    );
    assert.match(
      baseCss,
      /@media \(max-width: 980px\)[\s\S]*?\.athlete-top > \.athlete-history\s*\{[^}]*grid-column:\s*auto/,
    );
    assert.match(
      baseCss,
      /@media \(max-width: 980px\)[\s\S]*?\.athlete-top > \.athlete-history\s*\{[^}]*height:\s*var\(--card-height\)/,
    );
  });
});
