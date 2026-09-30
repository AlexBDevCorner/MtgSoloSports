import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const color = readFileSync(join(here, 'ColorCupPage.tsx'), 'utf8');
const type = readFileSync(join(here, 'TypeCupPage.tsx'), 'utf8');
const action = readFileSync(join(here, 'EventLiveAction.tsx'), 'utf8');

describe('cups are played on Live', () => {
  it('replaces one-click run buttons with Play on Live', () => {
    for (const page of [color, type]) {
      assert.ok(page.includes('<EventLiveAction'), 'page offers the Live action');
      assert.ok(!page.includes('runColorCupIndividual(') && !page.includes('runColorCupTeam(') && !page.includes('runTypeCupTeam('));
    }
    assert.ok(color.includes('eventKey="color-cup-individual"') && color.includes('eventKey="color-cup-team"'));
    assert.ok(type.includes('eventKey="type-cup-team"'));
    assert.ok(action.includes('Play on Live'));
    assert.ok(action.includes('livePath(saveId, { event: eventKey, season: progress.sourceSeasonNumber })'));
  });

  it('never shows results while an event is in progress', () => {
    assert.ok(action.includes('In progress'), 'in-progress state is explicit');
    assert.ok(action.includes('progressLabel('));
  });

  it('offers the next Cup even while an older Cup result is on the page', () => {
    for (const page of [color, type]) {
      assert.ok(!/(result|team) \? null : <EventLiveAction/.test(page), 'Live action is not hidden behind an older result');
    }
    assert.ok(action.includes('Season {progress.sourceSeasonNumber}'), 'the action names the season it will play');
  });
});
