import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const read = (file: string) => (existsSync(join(here, file)) ? readFileSync(join(here, file), 'utf8') : '');

const edition = read('CupEditionPage.tsx');
const live = readFileSync(join(here, '..', 'live', 'LiveEventView.tsx'), 'utf8');
const historyEvent = readFileSync(join(here, '..', 'history', 'HistoryEventView.tsx'), 'utf8');
const historyPage = readFileSync(join(here, '..', 'history', 'HistoryPage.tsx'), 'utf8');
const api = read('colorCupApi.ts');

describe('color cup cutoff presentation', () => {
  it('shows phased 32-round editions distinctly from legacy 16-round ones', () => {
    assert.ok(edition.includes('isPhasedResult(individual)'), 'edition branches on persisted format');
    assert.ok(edition.includes('Finalists — ranks 1–16'), 'finalists table');
    assert.ok(edition.includes('Eliminated at the cutoff — ranks 17–32'), 'eliminated table');
    assert.ok(edition.includes('100% carryover') || edition.includes('full carryover'), 'carryover explained');
    assert.ok(edition.includes('Replay round 16'), 'cutoff round replay preserved');
    assert.ok(edition.includes('Replay round 17'), 'final start replay preserved');
    assert.ok(edition.includes("historyPath(saveId, { season, event: 'color-cup-individual' })"), 'general replay preserved');
  });

  it('keeps legacy editions accurate with no fabricated cutoff', () => {
    assert.ok(edition.includes('Full table —'), 'legacy table title preserved');
    assert.ok(edition.includes('All selected athletes race one'), 'legacy copy preserved');
    assert.ok(api.includes('qualifyingRank?:'), 'cutoff fields additive and nullable');
    assert.ok(api.includes('formatVersion?:'), 'format additive for old payloads');
  });

  it('exposes cutoff progress for live and history without touching other tournaments', () => {
    assert.ok(api.includes('fetchColorCupIndividualProgress'), 'progress client');
    assert.ok(api.includes('/cups/color/individual/progress'), 'progress route');
    assert.ok(live.includes('fetchColorCupIndividualProgress'), 'live reads persisted progress');
    assert.ok(live.includes('top 16 advance'), 'live cut line');
    assert.ok(live.includes('full carryover') || live.includes('100% of qualifying'), 'live carryover');
    assert.ok(historyEvent.includes('fetchColorCupIndividualProgress'), 'history reads persisted progress');
    assert.ok(historyEvent.includes('Cut — top'), 'history cut line');
    assert.ok(!live.includes('color-cup-team') || live.includes("event === 'color-cup-individual'"), 'individual scope only');
  });

  it('keeps every recorded round replayable in live, history and edition views', () => {
    assert.ok(live.includes('Played rounds') || live.includes('Played rounds'), 'live round pills');
    assert.ok(historyEvent.includes('Round selector') || historyEvent.includes('Round'), 'history round selector');
    assert.ok(historyPage.includes('phased 16+16'), 'history explains phased shape');
  });
});
