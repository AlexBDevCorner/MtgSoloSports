import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const read = (relative: string) => readFileSync(join(here, relative), 'utf8');

const dashboard = read('../dashboard/DashboardPage.tsx');
const records = read('../records/RecordsPage.tsx');
const standings = read('../standings/StandingsPage.tsx');
const live = read('../live/LivePage.tsx');
const liveEvent = read('../live/LiveEventView.tsx');
const liveQualifier = read('../live/LiveQualifierView.tsx');
const history = read('../history/HistoryPage.tsx');
const historyEvent = read('../history/HistoryEventView.tsx');
const search = read('../athletes/AthleteSearchPage.tsx');
const qualifiers = read('../qualifiers/QualifiersPage.tsx');
const leagueProfile = read('./LeagueProfilePage.tsx');
const movement = read('../movement/MovementReveal.tsx');
const rebalance = read('../rebalance/RebalanceReveal.tsx');
const leagueLink = read('./LeagueLink.tsx');

describe('MSS-139 league, color and Cup team links', () => {
  it('dashboard links colors, league honours and cup editions with plain-text fallbacks', () => {
    assert.ok(dashboard.includes('ColorTeamLink'), 'leader colors link to the Color Cup team');
    assert.ok(dashboard.includes('row.sportingColor'), 'leader colors use the backend color identity');
    assert.ok(dashboard.includes('honour.leagueKey'), 'league honours use the backend league key');
    assert.ok(dashboard.includes('<LeagueLink'), 'league honours render a real league profile anchor');
    assert.ok(dashboard.includes('cupEditionPath(saveId, cup,'), 'cup honours link to the cup edition');
    assert.ok(dashboard.includes('honourCupKind'), 'cup vs league honours split on honour kind, never names');
  });

  it('records links career colors, honour leagues and team holders with fallbacks', () => {
    assert.ok(records.includes('ColorTeamLink'), 'career leader colors link to the Color Cup team');
    assert.ok(records.includes('row.leagueKey'), 'honours use the backend league key');
    assert.ok(records.includes('<LeagueLink'), 'honours render a real league profile anchor');
    assert.ok(records.includes('cupEditionPath(saveId, honourCupKind'), 'cup honours link to the cup edition');
    assert.ok(records.includes('ScoringTeamLink'), 'team holders link through one team component');
    assert.ok(records.includes('scoringTeamCup(recordKey'), 'team cup derives from the record key, never names');
    assert.ok(records.includes('cupTeamKey(cup,'), 'team keys validate before linking');
    assert.ok(records.includes('<TeamBadge'), 'team links render anchors through TeamBadge');
  });

  it('standings links matrix colors and keeps its league profile link', () => {
    assert.ok(standings.includes('ColorTeamLink'), 'matrix color sub-labels link to the Color Cup team');
    assert.ok(standings.includes('sportingColor={row.sportingColor}'), 'matrix rows carry the numeric color identity');
    assert.ok(standings.includes('leagueProfileKey'), 'standings keeps the league profile link');
    assert.ok(standings.includes('<Link to={leaguePath'), 'profile links use the existing route builder');
  });

  it('live links the standings title league and color cells without touching simulation', () => {
    assert.ok(live.includes('leagueProfileKey'), 'live derives the league key from backend tier identity');
    assert.ok(live.includes('leagueKeyFor({'), 'live builds the key from kind/division/color, never names');
    assert.ok(live.includes('sportingColor: league.sportingColor'), 'live uses the additive progress color');
    assert.ok(live.includes('League profile'), 'live adds a league profile link beside Full standings');
    assert.ok(live.includes('standingsLeaguePath'), 'live keeps the existing standings link');
    assert.ok(live.includes('ColorTeamLink'), 'live color cells link to the Color Cup team');
    assert.ok(live.includes('<RoundReveal'), 'live still replays persisted rounds only');
  });

  it('live and history cup team standings link through the event cup with plain-text fallbacks', () => {
    for (const [name, page] of [['LiveEventView', liveEvent], ['HistoryEventView', historyEvent]] as const) {
      assert.ok(page.includes('CupTeamCell'), `${name} links team rows through one team cell`);
      assert.ok(page.includes("event === 'color-cup-team'"), `${name} derives the cup from the event identity`);
      assert.ok(page.includes('cupTeamKey(cup,'), `${name} validates team keys before linking`);
      assert.ok(page.includes('<TeamBadge'), `${name} renders anchors through TeamBadge`);
      assert.ok(page.includes('return <>{teamName}</>'), `${name} keeps unknown teams as plain text`);
    }
  });

  it('history titles gain league profile links and keep standings links', () => {
    assert.ok(history.includes('selectedLeagueKey'), 'history derives the league key from competitions');
    assert.ok(history.includes('leagueKeyFor({'), 'history builds the key from tier identity, never names');
    assert.ok(history.includes('League profile'), 'history titles add a league profile link');
    assert.ok(history.includes('standingsLeaguePath'), 'history keeps the existing standings links');
    assert.ok(history.includes('cupChampionCell'), 'history cup champions link to their team page');
    assert.ok(history.includes('<TeamBadge'), 'history champion links render through TeamBadge');
  });

  it('athlete search links colors and current leagues with pool fallbacks', () => {
    assert.ok(search.includes('ColorTeamLink'), 'search color sub-labels link to the Color Cup team');
    assert.ok(search.includes('sportingColor={row.sportingColor}'), 'search colors use the numeric identity');
    assert.ok(search.includes('row.currentLeagueKey'), 'search leagues use the backend league key');
    assert.ok(search.includes('<LeagueLink'), 'search leagues render a real profile anchor');
    assert.ok(search.includes("'Common pool'"), 'pool athletes stay plain text');
  });

  it('qualifiers link entrant colors and source leagues with legacy fallbacks', () => {
    assert.ok(qualifiers.includes('ColorTeamLink'), 'qualifier entrant colors link to the Color Cup team');
    assert.ok(qualifiers.includes('row.fromLeagueKey'), 'qualifier entrants use the backend league key');
    assert.ok(qualifiers.includes('<LeagueLink'), 'qualifier sources render a real profile anchor');
    assert.ok(liveQualifier.includes('FromLeagueCell'), 'live qualifier rows link source leagues');
    assert.ok(liveQualifier.includes('fromLeagueKey'), 'live qualifier rows use the backend league key');
    assert.ok(liveQualifier.includes('<LeagueLink'), 'live qualifier sources render a real profile anchor');
  });

  it('a colored feeder league profile links to its color team', () => {
    assert.ok(leagueProfile.includes('colorTeamKeyFromSportingColor'), 'profile maps the persisted color to a team key');
    assert.ok(leagueProfile.includes('isFeeder'), 'only feeder leagues offer the color team link');
    assert.ok(leagueProfile.includes('ColorTeamLink'), 'profile links through the shared color team link');
    assert.ok(leagueProfile.includes('Color Cup team'), 'profile labels the destination');
  });

  it('reveals link only revealed headings and post-reveal summaries', () => {
    for (const [name, page] of [['MovementReveal', movement], ['RebalanceReveal', rebalance]] as const) {
      assert.ok(page.includes('LeagueName'), `${name} links league names through one local component`);
      assert.ok(page.includes('fromLeagueKey'), `${name} uses backend source keys`);
      assert.ok(page.includes('toLeagueKey'), `${name} uses backend destination keys`);
      assert.ok(page.includes('isComplete'), `${name} summary tables render only post-reveal`);
      assert.ok(page.includes('PendingTile'), `${name} keeps unrevealed tiles face-down without links`);
      assert.ok(page.includes('<LeagueLink'), `${name} renders real profile anchors`);
    }
    assert.ok(movement.includes('revealedBoundaryKey'), 'movement headings use only revealed steps');
    assert.ok(movement.includes('revealed.has(step.key)'), 'movement links never expose unrevealed placements');
    assert.ok(rebalance.includes('rebalanceLeagueKey'), 'rebalance headers derive keys from persisted triples');
    assert.ok(rebalance.includes('is-latest'), 'rebalance keeps its progressive reveal behavior');
  });

  it('no page builds league or cup URLs by hand or from display names', () => {
    for (const [name, page] of [
      ['DashboardPage', dashboard],
      ['RecordsPage', records],
      ['LivePage', live],
      ['LiveEventView', liveEvent],
      ['HistoryPage', history],
      ['AthleteSearchPage', search],
      ['QualifiersPage', qualifiers],
      ['MovementReveal', movement],
      ['RebalanceReveal', rebalance],
    ] as const) {
      assert.ok(!page.includes('/leagues/${'), `${name} has no hand-built league URL`);
      assert.ok(!page.includes('/cups/${'), `${name} has no hand-built cup URL`);
      assert.ok(!page.includes('leagueName.toLowerCase'), `${name} never parses league names for URLs`);
    }
  });

  it('the shared league link stays minimal and reusable', () => {
    assert.ok(leagueLink.includes('leaguePath(saveId, leagueKey)'), 'league link uses the existing route builder');
    assert.ok(leagueLink.includes('<Link'), 'league link renders a real anchor');
  });
});
