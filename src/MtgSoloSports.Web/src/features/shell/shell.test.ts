import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const srcRoot = join(here, '..', '..');

function read(rel: string): string {
  return readFileSync(join(srcRoot, rel), 'utf8');
}

// Read at module scope: a missing file must fail the run, and Node's test
// runner does not count exceptions thrown inside a describe body as failures.
const shell = read('features/shell/AppShell.tsx');
const css = read('shared/ui/base.css');
const drawer = read('features/shell/useRailDrawer.ts');
const button = read('features/shell/MenuButton.tsx');

describe('left-rail shell', () => {

  it('renders a rail with brand, save context, main nav and pinned saves/catalog', () => {
    for (const token of [
      'className="rail"',
      'rail-brand',
      'rail-save',
      'aria-label="Main"',
      'rail-nav-bottom',
      'rail-catalog',
    ]) {
      assert.ok(shell.includes(token), `shell renders ${token}`);
    }
  });

  it('marks the current page and disables save-scoped items without a save', () => {
    assert.ok(shell.includes('ariaCurrent'), 'current page keeps aria-current');
    assert.ok(shell.includes('aria-disabled="true"'), 'disabled items are announced');
    assert.ok(shell.includes('Select a save first'), 'disabled items explain why');
  });

  it('titles the content column from the view and drops the footer', () => {
    assert.ok(shell.includes('VIEW_TITLES'), 'page title derives from the view');
    assert.ok(shell.includes('page-title'), 'page header renders the title');
    assert.ok(!shell.includes('<footer'), 'footer chrome is gone');
  });

  it('keeps long save names from widening the rail', () => {
    assert.match(css, /\.rail-save-name\s*\{[^}]*text-overflow:\s*ellipsis/);
  });

  it('lets grids collapse to one column on phones without horizontal scroll', () => {
    assert.ok(css.includes('minmax(min(440px, 100%), 1fr)'), 'columns stay wide enough for 5-column tables');
  });

  it('colors plain links from the tokens instead of browser blue/purple', () => {
    assert.match(css, /:where\(a\)\s*\{[^}]*color:\s*var\(--accent\)/);
    assert.match(css, /:where\(a:visited\)\s*\{[^}]*color:\s*var\(--accent\)/);
  });

  it('shows catalog status in the rail instead of a content strip', () => {
    const app = read('App.tsx');
    assert.ok(app.includes('catalogStatus(catalog.stats, catalog.loading)'), 'App feeds the rail status');
    const banner = read('features/catalog/CatalogBanner.tsx');
    assert.ok(!banner.includes('catalog-strip'), 'the always-on catalog strip is gone');
    assert.ok(banner.includes('loading && !stats'), 'refreshes keep the actionable notice visible');
  });
});

describe('narrow-screen drawer', () => {

  it('toggle exposes its state and controls the rail', () => {
    assert.ok(button.includes('aria-expanded={open}'), 'toggle announces open state');
    assert.ok(button.includes('aria-controls={RAIL_ID}'), 'toggle names the rail it controls');
    assert.ok(shell.includes('id={RAIL_ID}'), 'rail carries the controlled id');
    assert.ok(shell.includes('<MenuButton'), 'mobile bar renders the toggle');
  });

  it('closes on Escape, backdrop click and navigation', () => {
    assert.ok(drawer.includes("'Escape'"), 'Escape closes');
    assert.ok(drawer.includes('removeEventListener'), 'key listener is cleaned up');
    assert.ok(shell.includes('rail-backdrop'), 'backdrop exists');
    assert.ok(shell.includes('onClickCapture'), 'link clicks inside the rail close it');
    assert.ok(shell.includes("closest('a')"), 'only link activations close the drawer');
  });

  it('hides the closed drawer from keyboard focus and respects reduced motion', () => {
    const mobile = css.slice(css.indexOf('@media (max-width: 1020px)'));
    assert.match(mobile, /\.rail\s*\{[^}]*visibility:\s*hidden/, 'closed rail is not focusable');
    assert.ok(mobile.includes('.app.rail-open .rail'), 'open state reveals the rail');
    assert.match(css, /prefers-reduced-motion[\s\S]*\.rail[\s\S]*transition:\s*none/, 'drawer motion can be disabled');
  });
});

describe('filter toolbar', () => {
  it('sticks to the top of the viewport and clears the mobile top bar', () => {
    assert.match(css, /\.toolbar\s*\{[^}]*position:\s*sticky/);
    const mobile = css.slice(css.indexOf('@media (max-width: 1020px)'));
    assert.match(mobile, /\.toolbar\s*\{[^}]*top:\s*var\(--topbar-h\)/);
  });

  it('keeps segmented controls on one line inside the toolbar', () => {
    assert.match(css, /\.toolbar \.field:has\(> \.segmented\)\s*\{[^}]*flex:\s*0 0 auto/);
    assert.match(css, /\.toolbar \.segmented\s*\{[^}]*flex-wrap:\s*nowrap/);
  });
});
