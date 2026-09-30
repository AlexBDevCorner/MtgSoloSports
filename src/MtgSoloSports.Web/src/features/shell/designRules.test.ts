import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const srcRoot = join(here, '..', '..');

function read(rel: string): string {
  return readFileSync(join(srcRoot, rel), 'utf8');
}

function stripComments(css: string): string {
  return css.replace(/\/\*[\s\S]*?\*\//g, '');
}

const TOKENS = 'shared/ui/tokens.css';

function listCss(rel = ''): string[] {
  const found: string[] = [];
  for (const entry of readdirSync(join(srcRoot, rel), { withFileTypes: true })) {
    const child = rel ? `${rel}/${entry.name}` : entry.name;
    if (entry.isDirectory()) {
      found.push(...listCss(child));
    } else if (entry.name.endsWith('.css')) {
      found.push(child);
    }
  }
  return found;
}

const STYLESHEETS = listCss();
// Read at module scope: a missing file must fail the run, and Node's test
// runner does not count exceptions thrown inside a describe body as failures.
const SOURCES = new Map(STYLESHEETS.map((file) => [file, stripComments(read(file))]));

describe('design rules: flat, square, dark', () => {
  for (const [file, css] of SOURCES) {
    it(`${file} has no gradients or shadows`, () => {
      assert.ok(!/gradient\(/i.test(css), 'no gradients');
      assert.ok(!/box-shadow/i.test(css), 'no shadows');
    });

    it(`${file} has only square corners`, () => {
      for (const match of css.matchAll(/border(?:-[a-z]+)*-radius\s*:\s*([^;]+);/gi)) {
        assert.ok(['0', 'var(--radius)'].includes(match[1].trim()), `${file}: border-radius ${match[1]}`);
      }
    });

    if (file !== TOKENS) {
      it(`${file} takes colors from tokens only`, () => {
        assert.ok(!/#[0-9a-f]{3,8}\b/i.test(css), 'no hex color literals');
        assert.ok(!/rgba?\(/i.test(css), 'no rgb color literals');
      });
    }
  }

  it('tokens define the dark palette with a single amber accent and zero radius', () => {
    const tokens = read(TOKENS);
    assert.match(tokens, /color-scheme:\s*dark/);
    assert.match(tokens, /--bg:\s*#0c0d0f/i);
    assert.match(tokens, /--accent:\s*#e8b44a/i);
    assert.match(tokens, /--radius:\s*0\s*;/);
  });

  it('discovers every stylesheet', () => {
    for (const file of [
      TOKENS,
      'shared/ui/base.css',
      'features/live/LivePage.css',
      'features/reveal/RevealBoard.css',
      'features/standings/StandingsPage.css',
    ]) {
      assert.ok(STYLESHEETS.includes(file), `${file} is guarded`);
    }
  });

  it('no stylesheet uses the retired variable names', () => {
    for (const [file, css] of SOURCES) {
      assert.ok(!/--panel\b|--panel-2\b|--accent-2\b/.test(css), `${file} uses tokens, not legacy names`);
    }
  });

  it('main.tsx loads tokens before base and the old global sheet is gone', () => {
    const main = read('main.tsx');
    const tokensAt = main.indexOf('./shared/ui/tokens.css');
    const baseAt = main.indexOf('./shared/ui/base.css');
    assert.ok(tokensAt >= 0 && baseAt > tokensAt, 'tokens load first');
    assert.ok(!main.includes('./styles.css'), 'styles.css is no longer imported');
    assert.ok(baseAt < main.indexOf("import App"), 'base styles load before feature styles so features win ties');
  });
});
