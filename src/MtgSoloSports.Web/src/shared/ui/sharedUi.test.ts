import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));

function read(name: string): string {
  return readFileSync(join(here, name), 'utf8');
}

const info = read('InfoDisclosure.tsx');
const card = read('Card.tsx');
const css = read('base.css');

describe('info disclosure', () => {
  it('is a native, labelled disclosure', () => {
    assert.ok(info.includes('<details className="info"'), 'uses native details for keyboard support');
    assert.ok(info.includes('aria-label="About this panel"'), 'icon-only summary has an accessible name');
    assert.ok(info.includes('info-body'), 'body is styled as a popover');
  });

  it('is available on every card through an optional info prop', () => {
    assert.ok(card.includes('info?: ReactNode'), 'Card accepts info');
    assert.ok(card.includes('<InfoDisclosure>'), 'Card renders the disclosure');
    assert.ok(card.includes('card-heading'), 'title block has its own class');
  });

  it('is styled as a compact square trigger with a floating body', () => {
    assert.match(css, /\.info > summary\s*\{[^}]*list-style:\s*none/);
    assert.match(css, /\.info-body\s*\{[^}]*position:\s*absolute/);
  });
});
