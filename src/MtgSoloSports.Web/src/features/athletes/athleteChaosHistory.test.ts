import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const read = (file: string) => readFileSync(join(here, file), 'utf8');

const page = read('AthleteProfilePage.tsx');
const api = read('athleteApi.ts');

describe('athlete chaos history', () => {
  it('exposes a chaos history model with attained stage, final place and trajectory', () => {
    assert.ok(api.includes('export interface AthleteChaosHistory'), 'chaos history interface');
    assert.ok(api.includes('export interface AthleteChaosStage'), 'chaos stage interface');
    for (const field of [
      'sourceSeasonNumber',
      'isPoolInvite',
      'sourceLabel',
      'stagesEntered',
      'isFinalist',
      'finalPlace',
      'medal',
      'stages:',
    ]) {
      assert.ok(api.includes(field), `field ${field}`);
    }
    assert.ok(api.includes('chaosHistory?: AthleteChaosHistory[]'), 'profile carries chaos history additively');
  });

  it('renders a chaos section with edition, stage path and replay links', () => {
    assert.ok(page.includes('Chaos Cup —'), 'chaos card');
    assert.ok(page.includes('chaosHistory.length'), 'count in title');
    assert.ok(page.includes('chaosAttainedLabel'), 'attained stage wording');
    assert.ok(page.includes('Group {stage.groupNumber}'), 'group replay link text');
    assert.ok(page.includes('chaosLivePath('), 'live group replay link');
    assert.ok(page.includes("cupEditionPath(saveId, 'chaos'"), 'edition link');
    assert.ok(page.includes('Provisional'), 'provisional wording');
    assert.ok(page.includes('Official'), 'official wording');
    assert.ok(page.includes('medalBadge(edition.medal)'), 'medal wording');
  });

  it('shows frozen source attribution and honest empty states', () => {
    assert.ok(page.includes('edition.sourceLabel'), 'frozen source tier or Pool');
    assert.ok(page.includes('Pool invite'), 'pool invite wording');
    assert.ok(page.includes('No Chaos Cup appearances yet.'), 'empty state copy');
    assert.ok(page.includes('chaosHistory.length === 0'), 'empty branch');
  });

  it('preserves the existing league and cup sections', () => {
    assert.ok(page.includes('Season-by-season'), 'league history card intact');
    assert.ok(page.includes('Cup history —'), 'cups card intact');
    assert.ok(page.includes('cupSelections'), 'selections section intact');
    assert.ok(page.includes('honours'), 'honours section intact');
  });
});
