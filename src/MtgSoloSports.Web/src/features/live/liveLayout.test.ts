import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const livePage = readFileSync(join(here, 'LivePage.tsx'), 'utf8');
const liveCss = readFileSync(join(here, 'LivePage.css'), 'utf8');
const reveal = readFileSync(join(here, '..', 'reveal', 'RoundReveal.tsx'), 'utf8');
const historyPage = readFileSync(join(here, '..', 'history', 'HistoryPage.tsx'), 'utf8');
const shellCss = readFileSync(join(here, '..', '..', 'shared', 'ui', 'base.css'), 'utf8');
const boardCss = readFileSync(join(here, '..', 'reveal', 'RevealBoard.css'), 'utf8');

describe('MSS-038 live above-the-fold composition', () => {
  it('renders management in a narrow sidebar and the reveal as the main hero', () => {
    assert.ok(livePage.includes('live-layout'), 'LivePage uses the side-by-side layout');
    assert.ok(livePage.includes('live-sidebar'), 'management lives in a compact side panel');
    assert.ok(livePage.includes('live-main'), 'reveal owns the main column');
    assert.ok(
      livePage.includes('<aside') && livePage.includes('aria-label="Competition management"'),
      'sidebar is a labelled landmark',
    );
  });

  it('keeps all live flows in the sidebar without full-width stacked panels', () => {
    for (const token of ['Next Round', 'Complete Stage', 'Completed rounds in this stage', 'round-pills', 'League']) {
      assert.ok(livePage.includes(token), `sidebar keeps ${token}`);
    }
    assert.ok(!livePage.includes('className="dashboard"'), 'vertical stacked dashboard wrapper is gone on Live');
    assert.ok(livePage.includes('gateReason'), 'gate/disabled explanations stay near actions');
    assert.ok(livePage.includes('actionError'), 'action errors stay near actions');
    assert.ok(livePage.includes('AthleteLink'), 'athlete profiles use correct-save links (MSS-040)');
    assert.ok(livePage.includes('urlLeagueId'), 'live league selection is URL-backed (MSS-040)');
  });

  it('opens live rounds manual-paused and steps with the same +1 control', () => {
    assert.ok(livePage.includes('autoPlayOnStart={false}'), 'live keeps the manual default');
    assert.ok(livePage.includes('layout="live"'), 'live opts into the compact reveal variant');
    assert.ok(reveal.includes("aria-label=\"Reveal one more card\""), 'primary manual +1 action is retained');
    assert.ok(reveal.includes('stepForward'), 'stepping still drives presentation state only');
  });

  it('gives the live reveal a compact toolbar and stable spotlight that never pushes the board', () => {
    assert.ok(reveal.includes('reveal-live-toolbar'), 'playback lives in one compact toolbar row');
    assert.ok(reveal.includes('reveal-live-latest'), 'latest-card chip sits adjacent to controls');
    assert.ok(reveal.includes('aria-live="polite"'), 'latest chip keeps the live progress announcement');
    assert.ok(reveal.includes('reveal-help'), 'lengthy help collapses into a disclosure');
    assert.ok(reveal.includes('reveal-meta'), 'checksum meta is a subdued line inside help');
    assert.ok(liveCss.includes('.reveal-live-latest'), 'inline latest chip is styled');
    assert.ok(
      /min-height:\s*40px/.test(liveCss),
      'latest chip reserves its height at 0/N so +1 never shifts the board',
    );
    assert.ok(reveal.includes('onError'), 'spotlight artwork falls back when images fail to load');
  });

  it('uses the full desktop width with a dominant main column', () => {
    assert.match(
      shellCss,
      /\.app\s*\{[^}]*grid-template-columns:\s*var\(--rail-width\)\s+minmax\(0,\s*1fr\)/,
      'shell is a rail plus a full-width content column on every page',
    );
    assert.ok(!/\.app\s*\{[^}]*max-width/.test(shellCss), 'no page is confined to a centered column');
    assert.ok(liveCss.includes('clamp(240px'), 'sidebar stays narrow while the board dominates');
    assert.ok(liveCss.includes('minmax(0, 1fr)'), 'board owns the flexible main column');
  });

  it('keeps eight legible tiles per row on wide desktops and reflows on narrow screens', () => {
    assert.ok(boardCss.includes('repeat(8'), '32-card board supports 8 across where space permits');
    assert.ok(boardCss.includes('1600px'), 'eight-across breakpoint is preserved from MSS-035');
    assert.ok(liveCss.includes('max-width: 1020px'), 'tablet collapses the side layout');
    assert.ok(liveCss.includes('max-width: 640px'), 'phones get a touch-friendly single column');
    assert.ok(liveCss.includes('order: -1'), 'reveal stays prominent on narrow layouts');
  });

  it('leaves History replay semantics and presentation untouched', () => {
    assert.ok(!historyPage.includes('layout="live"'), 'history never opts into the live sidebar variant');
    assert.ok(!historyPage.includes('live-sidebar'), 'history keeps its own navigation layout');
    assert.ok(reveal.includes("layout = 'default'"), 'shared reveal defaults to the History presentation');
  });
});
