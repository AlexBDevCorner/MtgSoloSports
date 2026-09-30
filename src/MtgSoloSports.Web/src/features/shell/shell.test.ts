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

describe('left-rail shell', () => {
  const shell = read('features/shell/AppShell.tsx');
  const css = read('shared/ui/base.css');

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
    assert.ok(css.includes('minmax(min(380px, 100%), 1fr)'));
  });

  it('shows catalog status in the rail instead of a content strip', () => {
    const app = read('App.tsx');
    assert.ok(app.includes('catalogStatus(catalog.stats, catalog.loading)'), 'App feeds the rail status');
    const banner = read('features/catalog/CatalogBanner.tsx');
    assert.ok(!banner.includes('catalog-strip'), 'the always-on catalog strip is gone');
    assert.ok(banner.includes('loading && !stats'), 'refreshes keep the actionable notice visible');
  });
});
