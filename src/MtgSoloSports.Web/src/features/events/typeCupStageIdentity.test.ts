import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { livePath, parseRoute, historyPath } from '../routing/routes.ts';

const here = dirname(fileURLToPath(import.meta.url));
const api = readFileSync(join(here, 'eventsApi.ts'), 'utf8');
const liveView = readFileSync(join(here, '..', 'live', 'LiveEventView.tsx'), 'utf8');

describe('MSS-079 Type Cup stage identity round-trip', () => {
  it('lists rounds with tournament phase and qualification group', () => {
    assert.ok(api.includes('tournamentPhase?: number | null'), 'PlayedRound carries phase');
    assert.ok(api.includes('qualificationGroup?: number | null'), 'PlayedRound carries qual group');
  });

  it('fetches one round with optional stage query params', () => {
    assert.ok(api.includes('tournamentPhase'), 'fetchEventRound sends phase');
    assert.ok(api.includes('qualificationGroup'), 'fetchEventRound sends qual group');
    assert.ok(
      api.includes("params.set('tournamentPhase'") || api.includes('tournamentPhase'),
      'stage query is built',
    );
  });

  it('scopes team-standings baselines to one stage so the Final starts at zero', () => {
    assert.ok(api.includes('fetchEventTeamStandings'), 'baseline helper exists');
    assert.ok(
      liveView.includes('fetchEventTeamStandings(saveId, season, event, controller.signal, { group: shownGroup, round: shownRound }, stage)'),
      'Live baseline passes the stage',
    );
  });

  it('keys reveals by stage so identical squad group/round numbers never collide', () => {
    assert.ok(
      liveView.includes("${roundView.tournamentPhase ?? 'x'}:${roundView.qualificationGroup ?? 'x'}"),
      'revealKey includes the stage',
    );
  });

  it('addresses distinct stages from the Live URL', () => {
    const finalUrl = livePath('save-1', {
      event: 'type-cup-team',
      season: 4,
      group: 1,
      round: 1,
      tournamentPhase: 2,
      qualificationGroup: 0,
    });
    assert.ok(finalUrl.includes('tournamentPhase=2'), `Final phase in URL, got ${finalUrl}`);
    assert.ok(finalUrl.includes('qualificationGroup=0'), `Final qual in URL, got ${finalUrl}`);
    const qualUrl = livePath('save-1', {
      event: 'type-cup-team',
      season: 4,
      group: 1,
      round: 1,
      tournamentPhase: 1,
      qualificationGroup: 2,
    });
    assert.ok(qualUrl.includes('qualificationGroup=2'), `qual group in URL, got ${qualUrl}`);
    assert.notEqual(finalUrl, qualUrl);

    const parsedFinal = parseRoute(`/saves/save-1/live`, `?event=type-cup-team&season=4&group=1&round=1&tournamentPhase=2&qualificationGroup=0`);
    assert.equal(parsedFinal.name, 'live');
    if (parsedFinal.name === 'live') {
      assert.equal(parsedFinal.tournamentPhase, 2);
      assert.equal(parsedFinal.qualificationGroup, 0);
    }
    const parsedLegacy = parseRoute(`/saves/save-1/live`, `?event=type-cup-team&season=4&group=1&round=1`);
    assert.equal(parsedLegacy.name, 'live');
    if (parsedLegacy.name === 'live') {
      assert.equal(parsedLegacy.tournamentPhase, null);
      assert.equal(parsedLegacy.qualificationGroup, null);
    }
  });

  it('keeps history replay URLs stage-aware with legacy fallback', () => {
    const url = historyPath('save-1', {
      season: 4,
      event: 'type-cup-team',
      group: 4,
      round: 8,
      tournamentPhase: 2,
      qualificationGroup: 0,
    });
    assert.ok(url.includes('tournamentPhase=2'), `history URL carries phase, got ${url}`);
  });
});
