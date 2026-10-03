import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const read = (file: string) => (existsSync(join(here, file)) ? readFileSync(join(here, file), 'utf8') : '');

const hub = read('CupsHubPage.tsx');
const action = read('EventLiveAction.tsx');
const edition = read('CupEditionPage.tsx');
const team = read('CupTeamPage.tsx');

describe('cups hub', () => {
  it('lists editions and teams from the editions endpoint', () => {
    assert.ok(hub.includes('fetchCupEditions(saveId'));
    assert.ok(hub.includes('cupEditionPath(saveId'));
    assert.ok(hub.includes('<TeamBadge'));
    assert.ok(hub.includes('No Cups have been played yet.'));
  });

  it('keeps Cups played on Live: the hub only links there', () => {
    for (const key of ['color-cup-individual', 'color-cup-team', 'type-cup-team']) {
      assert.ok(hub.includes(`eventKey="${key}"`), `offers ${key} on Live`);
    }
    assert.ok(hub.includes('Announce on Live'));
    assert.ok(hub.includes('selectionForAction('));
    assert.ok(!/runColorCup|runTypeCup/.test(hub), 'no one-click run from the hub');
    assert.ok(action.includes('Play on Live'));
    assert.ok(action.includes('In progress'));
  });

  it('never nests a team link inside an edition card link', () => {
    const card = hub.slice(hub.indexOf('function EditionCard'), hub.indexOf('function TeamTable'));
    assert.ok(card.includes('<TeamMark'), 'edition cards use the non-link mark');
    assert.ok(!card.includes('<TeamBadge'), 'no link inside the card link');
  });

  it('uses the page grid with notes behind info', () => {
    assert.ok(hub.includes('className="page-grid"'));
    assert.ok(hub.includes('info={'));
  });
});

describe('cup edition page', () => {
  it('reads one stored edition and never runs a Cup', () => {
    assert.ok(edition.includes('fetchSelectionReport(saveId, selectionKeyFor(cup), season'));
    assert.ok(edition.includes('fetchColorCupIndividual(saveId, season'));
    assert.ok(edition.includes('fetchColorCupTeam(saveId, season') && edition.includes('fetchTypeCupTeam(saveId, season'));
    assert.ok(!/runColorCup|runTypeCup|announceSelection/.test(edition));
  });

  it('treats a missing result as not played yet, and a missing edition as recoverable', () => {
    assert.ok((edition.match(/optional\(/g) ?? []).length >= 3, 'result fetches tolerate 404');
    assert.ok(edition.includes('This Cup has not been played'));
    assert.ok(edition.includes('cupsPath(saveId)'));
  });

  it('links to teams, athletes, the History replay and the selection explanation', () => {
    assert.ok(edition.includes('<TeamBadge'));
    assert.ok(edition.includes('<AthleteLink'));
    assert.ok(edition.includes('historyPath(saveId, { season, event: teamEventKey(cup), group:'));
    assert.ok(edition.includes("historyPath(saveId, { season, event: 'color-cup-individual' })"));
    assert.ok(edition.includes('livePath(saveId, { event: selectionKeyFor(cup), season })'));
    assert.ok(edition.includes('cupEditionPath(saveId, cup,'), 'previous and next edition links');
  });

  it('shows the individual event only for the Color Cup', () => {
    assert.ok(edition.includes("cup === 'color'"));
  });
});

describe('cup team page', () => {
  it('reads the team history and recovers from an unknown team', () => {
    assert.ok(team.includes('fetchCupTeamHistory(saveId, cup, teamKey'));
    assert.ok(team.includes('failure.status === 404'));
    assert.ok(team.includes('No such team'));
    assert.ok(team.includes('cupsPath(saveId)'));
  });

  it('shows honours, a block per season with the squad, and the all-time roster', () => {
    for (const text of ['Cups played', 'Best finish', 'Group wins', 'Round wins', 'All-time roster', 'Caps']) {
      assert.ok(team.includes(text), `shows ${text}`);
    }
    assert.ok(team.includes('<SquadTiles'));
    assert.ok(team.includes('rankOf(season.teamRank, season.teamCount)'));
    assert.ok(team.includes('reasonLabel(member.reason)'));
  });

  it('links each season to its edition and each leg to its History replay', () => {
    assert.ok(team.includes('cupEditionPath(saveId, cup, season.sourceSeasonNumber)'));
    assert.ok(team.includes('historyPath(saveId, {'));
    assert.ok(team.includes('group: member.leg.groupNumber'));
    assert.ok(team.includes('<AthleteLink'));
  });

  it('copes with a season that has no result yet', () => {
    assert.ok(team.includes('season.teamRank !== null'));
    assert.ok(team.includes('member.leg ?'));
  });
});
