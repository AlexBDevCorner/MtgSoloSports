import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const read = (file: string) => readFileSync(join(here, file), 'utf8');

const page = read('AthleteProfilePage.tsx');
const api = read('athleteApi.ts');
const leagueLink = read('../leagues/LeagueLink.tsx');

describe('MSS-138 athlete profile links', () => {
  it('exposes additive link identities without breaking older payloads', () => {
    for (const field of [
      'sportingColorTeamKey',
      'currentLeagueKey',
      'leagueKey',
      'fromLeagueKey',
      'toLeagueKey',
      'teamKey',
    ]) {
      assert.ok(api.includes(field), `athlete api carries ${field}`);
    }
    assert.ok(api.includes('cup?:'), 'cup identity is optional');
    // New profile link identities are optional so older saves still return valid profiles.
    assert.ok(api.includes('leagueKey?:'), 'league keys stay optional, never required');
    assert.ok(api.includes('teamKey?:'), 'selection team keys stay optional, never required');
    assert.ok(api.includes('sportingColorTeamKey?:'), 'color team keys stay optional');
    assert.ok(api.includes('currentLeagueKey?:'), 'current league keys stay optional');
  });

  it('links Color and nationality through the existing team badge', () => {
    assert.ok(page.includes('TeamBadge'), 'team links reuse TeamBadge');
    assert.ok(page.includes('card.sportingColorTeamKey'), 'color uses the backend team key');
    assert.ok(page.includes('cup="color"'), 'color links to the Color Cup team page');
    assert.ok(page.includes('card.typeCupNationality'), 'nationality uses the persisted nationality');
    assert.ok(page.includes('cup="type"'), 'nationality links to the Type Cup team page');
    assert.ok(page.includes('Uncapped'), 'uncapped athletes stay plain text');
  });

  it('links the current league to its profile and keeps pool athletes plain', () => {
    assert.ok(page.includes('career.currentLeagueKey'), 'current league uses the backend league key');
    assert.ok(page.includes('LeagueLink'), 'league links reuse the shared component');
    assert.ok(page.includes('Current league'), 'identity block keeps its label');
    assert.ok(page.includes("'—'"), 'retired and pool athletes stay plain text');
  });

  it('links season leagues to their profile and table, keeping pool rows plain', () => {
    assert.ok(page.includes('season.leagueKey'), 'season rows use the backend league key');
    assert.ok(page.includes('standingsLeaguePath(saveId, season.leagueId'), 'season rows link to that season table');
    assert.ok(page.includes('Table'), 'table link keeps a compact label');
    assert.ok(page.includes("'Pool'"), 'pool rows stay plain text');
  });

  it('links honours to league profiles and cup editions', () => {
    assert.ok(page.includes('honour.leagueKey'), 'league honours use the backend league key');
    assert.ok(page.includes('cupEditionPath(saveId, honourCup'), 'cup honours link to the cup edition');
    assert.ok(page.includes('honourKindLabel'), 'honour wording is unchanged');
  });

  it('links both movement endpoints and keeps the common pool plain', () => {
    assert.ok(page.includes('movement.fromLeagueKey'), 'movement source uses the backend key');
    assert.ok(page.includes('movement.toLeagueKey'), 'movement destination uses the backend key');
    assert.ok(page.includes('Common pool'), 'pool sides stay plain text');
    assert.ok(page.includes('movementTierPrefix'), 'tier prefixes are preserved');
  });

  it('links cup selections and cup history to team pages and editions', () => {
    assert.ok(page.includes('selection.teamKey'), 'selections use the backend team key');
    assert.ok(page.includes('cupEditionPath(saveId, teamCup'), 'selection seasons link to the edition');
    assert.ok(page.includes('entry.teamKey'), 'cup history uses the persisted team key');
    assert.ok(page.includes('cupEditionPath(saveId, historyCup'), 'cup history seasons link to the edition');
    assert.ok(page.includes('cupLabel(selection.cupKind)'), 'selection cup wording is unchanged');
  });

  it('uses real router anchors and never builds a guessed URL', () => {
    assert.ok(page.includes('<Link'), 'season and edition links are real anchors');
    assert.ok(page.includes('TeamBadge'), 'team links render anchors through TeamBadge');
    assert.ok(page.includes('LeagueLink'), 'league links render anchors through LeagueLink');
    assert.ok(!page.includes('/leagues/${'), 'no hand-built league URL');
    assert.ok(!page.includes('/cups/${'), 'no hand-built cup URL');
    assert.ok(!page.includes('leagueName.toLowerCase'), 'never parses league names for URLs');
    assert.ok(!page.includes('team.toLowerCase'), 'never parses display strings for URLs');
  });

  it('provides a small reusable league link for the next task', () => {
    assert.ok(leagueLink.includes('leaguePath(saveId, leagueKey)'), 'league link uses the existing route builder');
    assert.ok(leagueLink.includes('<Link'), 'league link renders a real anchor');
    assert.ok(leagueLink.includes('MSS-139') || leagueLink.includes('MSS-138'), 'league link documents reuse');
  });
});
