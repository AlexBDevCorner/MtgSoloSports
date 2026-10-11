import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import React from 'react';
import { create, act } from 'react-test-renderer';
import { Card } from './Card.tsx';

(globalThis as unknown as { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
(globalThis as unknown as { React: typeof React }).React = React;

const here = dirname(fileURLToPath(import.meta.url));
const srcRoot = join(here, '..', '..');
const read = (rel: string): string => readFileSync(join(srcRoot, rel), 'utf8');

const baseCss = read('shared/ui/base.css');
const tokensCss = read('shared/ui/tokens.css');
const dashboard = read('features/dashboard/DashboardPage.tsx');
const athlete = read('features/athletes/AthleteProfilePage.tsx');
const records = read('features/records/RecordsPage.tsx');

function renderCard(props: Record<string, unknown>): Record<string, unknown> {
  let renderer: ReturnType<typeof create> | null = null;
  act(() => {
    renderer = create(React.createElement(Card, props as never));
  });
  assert.ok(renderer);
  return (renderer as unknown as { toJSON: () => Record<string, unknown> }).toJSON() as Record<string, unknown>;
}

interface JsonNode {
  type?: string;
  props?: Record<string, unknown>;
  children?: Array<JsonNode | string> | null;
}

function findAll(node: JsonNode | string | null, predicate: (n: JsonNode) => boolean, out: JsonNode[] = []): JsonNode[] {
  if (node === null || typeof node === 'string') {
    return out;
  }
  if (predicate(node)) {
    out.push(node);
  }
  for (const child of node.children ?? []) {
    findAll(child, predicate, out);
  }
  return out;
}

describe('MSS-073 bounded panel layout', () => {
  it('renders one always-visible header plus a single body container', () => {
    const json = renderCard({ title: 'Panel', eyebrow: 'Test', children: 'Body content' }) as unknown as JsonNode;
    assert.equal(json.type, 'section');
    const headers = findAll(json, (n) => n.type === 'header');
    assert.equal(headers.length, 1, 'exactly one card header');
    const bodies = findAll(
      json,
      (n) => n.type === 'div' && String((n.props?.['className'] as string) ?? '').split(' ').includes('card-body'),
    );
    assert.equal(bodies.length, 1, 'exactly one scrollable body, no nested vertical scroll areas');
    const headerText = JSON.stringify(headers[0]);
    assert.ok(headerText.includes('Panel'), 'title stays in the header');
    const bodyText = JSON.stringify(bodies[0]);
    assert.ok(bodyText.includes('Body content'), 'content stays in the body');
  });

  it('passes span and header classes through the shared Card shell', () => {
    const wide = renderCard({ title: 'History', className: 'span-wide', children: 'rows' }) as unknown as JsonNode;
    assert.ok(String(wide.props?.['className'] ?? '').includes('span-wide'), 'wide span reaches the grid child');
    assert.ok(String(wide.props?.['className'] ?? '').includes('card'), 'base card class retained');
    const header = renderCard({ title: 'Name', className: 'athlete-header', children: 'hero' }) as unknown as JsonNode;
    assert.ok(String(header.props?.['className'] ?? '').includes('athlete-header'), 'compact header class retained');
  });

  it('defines one reusable bounded panel height token', () => {
    assert.ok(tokensCss.includes('--card-height'), 'panel height token exists');
    assert.match(tokensCss, /--card-height:\s*360px/);
  });

  it('bounds panel rows instead of growing with the longest card', () => {
    assert.match(baseCss, /\.page-grid--panels\s*\{[^}]*grid-auto-rows:\s*var\(--card-height\)/);
    assert.match(baseCss, /\.page-grid--panels\s*\{[^}]*grid-auto-flow:\s*dense/);
    assert.match(baseCss, /\.page-grid--panels\s*\{[^}]*align-items:\s*stretch/);
    assert.match(baseCss, /\.page-grid--panels\s*\{[^}]*repeat\(2,\s*minmax\(0,\s*1fr\)\)/);
  });

  it('keeps the panel shell bounded with one independently scrolling body', () => {
    assert.match(baseCss, /\.page-grid--panels > \.card\s*\{[^}]*display:\s*flex/);
    assert.match(baseCss, /\.page-grid--panels > \.card\s*\{[^}]*min-height:\s*0/);
    assert.match(baseCss, /\.page-grid--panels > \.card\s*\{[^}]*overflow:\s*hidden/);
    assert.match(baseCss, /\.page-grid--panels > \.card > \.card-body\s*\{[^}]*overflow-y:\s*auto/);
    assert.match(baseCss, /\.page-grid--panels > \.card > \.card-body\s*\{[^}]*min-height:\s*0/);
    assert.match(baseCss, /\.page-grid--panels > \.card > \.card-head\s*\{[^}]*flex:\s*0 0 auto/);
  });

  it('exposes a double-width span for genuinely wide tables', () => {
    assert.match(baseCss, /\.page-grid--panels > \.span-wide\s*\{[^}]*grid-column:\s*span 2/);
  });

  it('lets wide-table metadata wrap instead of forcing horizontal scroll', () => {
    assert.match(baseCss, /\.page-grid--panels > \.span-wide \.card-sub\s*\{[^}]*white-space:\s*normal/);
    assert.match(baseCss, /\.page-grid--panels > \.span-wide \.data-table td\.numeric/);
  });

  it('keeps dark-theme scrollbars and focus visible without scroll trapping', () => {
    assert.match(baseCss, /\.page-grid--panels > \.card > \.card-body\s*\{[^}]*scrollbar-width:\s*thin/);
    assert.match(baseCss, /\.page-grid--panels > \.card > \.card-body::-webkit-scrollbar-thumb\s*\{[^}]*background:\s*var\(--line-strong\)/);
    assert.match(baseCss, /\.page-grid--panels > \.card > \.card-body:focus-visible\s*\{[^}]*outline:\s*2px solid var\(--accent\)/);
    assert.match(baseCss, /\.page-grid--panels > \.card > \.card-body\s*\{[^}]*overscroll-behavior:\s*auto/);
    assert.ok(!baseCss.includes('overscroll-behavior: contain'), 'no scroll trapping via contain');
  });

  it('reflows across desktop, laptop/tablet and mobile without page overflow', () => {
    assert.ok(baseCss.includes('@media (min-width: 1700px)'), 'wide desktop breakpoint');
    assert.match(baseCss, /@media \(min-width: 1700px\)[\s\S]*?repeat\(3,\s*minmax\(0,\s*1fr\)\)/);
    assert.ok(baseCss.includes('@media (max-width: 980px)'), 'laptop/tablet breakpoint');
    assert.match(baseCss, /@media \(max-width: 980px\)[\s\S]*?\.page-grid--panels\s*\{[^}]*grid-template-columns:\s*minmax\(0,\s*1fr\)/);
    assert.match(baseCss, /@media \(max-width: 980px\)[\s\S]*?\.page-grid--panels > \.span-wide\s*\{[^}]*grid-column:\s*auto/);
    assert.match(baseCss, /@media \(max-width: 640px\)[\s\S]*?\.page-grid--panels/);
  });

  it('keeps the athlete identity header compact instead of full-width empty', () => {
    assert.match(baseCss, /\.athlete-header\s*\{[^}]*width:\s*min\(100%,\s*860px\)/);
    assert.match(baseCss, /\.athlete-header \.card-art-large\s*\{[^}]*width:\s*120px/);
    assert.ok(athlete.includes('className="athlete-header"'), 'profile header opts into the compact treatment');
  });

  it('opts Dashboard and Athlete grids into the bounded system', () => {
    assert.ok(dashboard.includes('page-grid page-grid--panels'), 'dashboard grid is bounded');
    assert.ok(athlete.includes('page-grid page-grid--panels'), 'athlete grid is bounded');
  });

  it('gives History and Cups double width while preserving their columns', () => {
    const historyAt = athlete.indexOf('eyebrow="History"');
    const cupsAt = athlete.indexOf('eyebrow="Cups"');
    assert.ok(historyAt > 0 && cupsAt > historyAt, 'both panels render');
    assert.ok(athlete.slice(historyAt, historyAt + 400).includes('span-wide'), 'History spans two columns');
    assert.ok(athlete.slice(cupsAt, cupsAt + 400).includes('span-wide'), 'Cups spans two columns');
    for (const label of ['Season', 'Cup / Event', 'Team', 'Place', 'Result', 'Champ pts']) {
      assert.ok(athlete.includes(label), `column preserved: ${label}`);
    }
    assert.ok(athlete.includes('medalBadge(entry.medal)'), 'interactive badges preserved');
  });

  it('leaves unrelated grids on the legacy auto-fit layout', () => {
    assert.ok(records.includes('className="page-grid"'), 'records keeps its grid');
    assert.ok(!records.includes('page-grid--panels'), 'bounded treatment stays scoped to Dashboard/Athlete');
  });
});
