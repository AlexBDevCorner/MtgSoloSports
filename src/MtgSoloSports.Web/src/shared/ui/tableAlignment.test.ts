import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const srcRoot = join(here, '..', '..');
const read = (rel: string): string => readFileSync(join(srcRoot, rel), 'utf8');

/**
 * MSS-046: every user-facing table follows the shared alignment convention
 * documented in base.css. Text/status columns stay on the left default in
 * both header and body; every rank/score/points/stat column carries .numeric
 * on BOTH its <th> and its <td>s so headers line up with their values.
 *
 * This test parses each table's header row and its first body-row template
 * positionally, so a future column that adds a numeric cell without the
 * matching numeric header (or vice versa) fails here instead of shipping a
 * visibly offset table.
 */
const AUDITED_TABLES = [
  'features/athletes/AthleteProfilePage.tsx',
  'features/athletes/AthleteSearchPage.tsx',
  'features/cups/CupEditionPage.tsx',
  'features/cups/CupSelectionView.tsx',
  'features/cups/CupTeamPage.tsx',
  'features/cups/CupsHubPage.tsx',
  'features/dashboard/DashboardPage.tsx',
  'features/history/HistoryEventView.tsx',
  'features/history/HistoryPage.tsx',
  'features/live/LiveEventView.tsx',
  'features/live/LivePage.tsx',
  'features/movement/MovementReveal.tsx',
  'features/rebalance/RebalanceReveal.tsx',
  'features/records/RecordsPage.tsx',
  'features/reveal/RoundReveal.tsx',
  'features/standings/StandingsPage.tsx',
];

interface Column {
  numeric: boolean;
  label: string;
}

function headerColumns(thead: string): Column[] {
  const columns: Column[] = [];
  for (const match of thead.matchAll(/<th([\s\S]*?)>([\s\S]*?)<\/th>/g)) {
    columns.push({
      numeric: /numeric/.test(match[1]),
      label: match[2].replace(/<[^>]+>/g, '').replace(/\s+/g, ' ').trim(),
    });
  }
  return columns;
}

function bodyColumns(firstRow: string, headerCount: number): Column[] {
  const columns: Column[] = [];
  for (const match of firstRow.matchAll(/<td([\s\S]*?)>/g)) {
    columns.push({ numeric: /numeric/.test(match[1]), label: '' });
  }
  // One textual <td> inside a .map() can render several columns (the Cup
  // selection ranking renders one numeric cell per rating component).
  // Such cells carry key={...}; expand them to fill the header row.
  while (columns.length < headerCount) {
    const dynamic = columns.findIndex((column, index) => /key=\{/.test(tdTag(firstRow, index)));
    assert.ok(dynamic >= 0, 'extra header columns must come from a mapped <td key={...}>');
    columns.splice(dynamic, 0, { ...columns[dynamic]! });
  }
  return columns;
}

function tdTag(firstRow: string, index: number): string {
  const tags = [...firstRow.matchAll(/<td([\s\S]*?)>/g)];
  return tags[index]?.[1] ?? '';
}

function dataTables(source: string): Array<{ attrs: string; body: string }> {
  const tables: Array<{ attrs: string; body: string }> = [];
  for (const match of source.matchAll(/<table([\s\S]*?)<\/table>/g)) {
    tables.push({ attrs: match[1], body: match[0] });
  }
  return tables;
}

describe('table alignment convention (MSS-046)', () => {
  it('documents the convention alongside the shared table styles', () => {
    assert.ok(read('shared/ui/base.css').includes('Table alignment convention'));
    assert.match(read('shared/ui/base.css'), /\.numeric\s*\{[^}]*text-align:\s*right/);
  });

  for (const file of AUDITED_TABLES) {
    it(`${file} aligns every header with its column values`, () => {
      const source = read(file);
      const tables = dataTables(source).filter((table) => !table.attrs.includes('matrix-table'));
      assert.ok(tables.length > 0, 'expected at least one audited table');
      for (const table of tables) {
        const thead = table.body.match(/<thead>([\s\S]*?)<\/thead>/)?.[1];
        assert.ok(thead, 'table has a header row');
        const headers = headerColumns(thead);
        const tbody = table.body.match(/<tbody>([\s\S]*?)<\/tbody>/)?.[1];
        assert.ok(tbody, 'table has a body');
        const firstRow = tbody.match(/<tr[\s\S]*?>([\s\S]*?)<\/tr>/)?.[1];
        assert.ok(firstRow, 'table has a body row template');
        const cells = bodyColumns(firstRow, headers.length);
        assert.equal(cells.length, headers.length, `header/cell count for ${file}`);
        headers.forEach((header, index) => {
          assert.equal(
            cells[index]!.numeric,
            header.numeric,
            `${file} column ${index} (“${header.label}”): header numeric=${header.numeric} but cells numeric=${cells[index]!.numeric}`,
          );
        });
      }
    });
  }

  it('keeps the standings matrix sticky numeric headers right-aligned', () => {
    const page = read('features/standings/StandingsPage.tsx');
    for (const sticky of ['sticky-rank', 'sticky-points', 'sticky-wins', 'sticky-bonus']) {
      assert.ok(
        page.includes(`numeric sticky ${sticky}`),
        `matrix header ${sticky} shares the numeric alignment of its cells`,
      );
    }
  });

  it('keeps the intentionally centered matrix stage columns centered on both sides', () => {
    const css = read('features/standings/StandingsPage.css');
    assert.match(css, /\.matrix-stage-head\s*\{[^}]*text-align:\s*center/);
    assert.match(css, /\.matrix-cell\s*\{[^}]*text-align:\s*center/);
  });
});
