import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  fastForwardAvailability,
  fastForwardConfirmationText,
  remainingGlobalStages,
} from './fastForwardSeasonHelpers.ts';
import type {
  SeasonProgress,
  SeasonProgressLeague,
  SeasonStatus,
} from './dashboardApi.ts';

const here = dirname(fileURLToPath(import.meta.url));
const component = readFileSync(join(here, 'FastForwardSeason.tsx'), 'utf8');
const helpers = readFileSync(join(here, 'fastForwardSeasonHelpers.ts'), 'utf8');
const api = readFileSync(join(here, 'dashboardApi.ts'), 'utf8');
const dashboardPage = readFileSync(join(here, 'DashboardPage.tsx'), 'utf8');
const livePage = readFileSync(join(here, '..', 'live', 'LivePage.tsx'), 'utf8');
const seasonFlow = readFileSync(join(here, 'SeasonFlow.tsx'), 'utf8');
const shell = readFileSync(join(here, '..', 'shell', 'AppShell.tsx'), 'utf8');

function league(
  leagueId: number,
  overrides?: Partial<SeasonProgressLeague>,
): SeasonProgressLeague {
  return {
    leagueId,
    leagueName: `League ${leagueId}`,
    leagueKind: 'Feeder',
    currentStage: 1,
    completedStages: 0,
    isLeagueComplete: false,
    ...overrides,
  };
}

function progress(overrides?: Partial<SeasonProgress>): SeasonProgress {
  return {
    saveId: 'save-1',
    seasonNumber: 1,
    globalStage: 1,
    isSeasonComplete: false,
    leagues: [league(1), league(2)],
    ...overrides,
  };
}

function status(overrides?: Partial<SeasonStatus>): SeasonStatus {
  return {
    saveId: 'save-1',
    currentSeasonNumber: 1,
    persistedPhase: 'SeasonInProgress',
    computedPhase: 'SeasonInProgress',
    sourceSeasonNumber: null,
    nextSeasonNumber: null,
    isInauguralTransition: false,
    globalStage: 1,
    isCurrentSeasonComplete: false,
    seasonComplete: false,
    movementResolved: false,
    qualifierResolved: false,
    rebalanced: false,
    cupSelectionResolved: false,
    cupIndividualResolved: false,
    cupTeamResolved: false,
    cupComplete: false,
    readyToStartNextSeason: false,
    expectedCup: 'ColorCup',
    legalNextActions: ['CompleteNextGlobalStage'],
    nextActionDetail: 'Complete the global stage.',
    eventProgress: null,
    ...overrides,
  };
}

describe('MSS-042 fast-forward availability', () => {
  it('is available on a fresh save with all 32 global stages remaining', () => {
    const seen = fastForwardAvailability(progress(), status());
    assert.equal(seen.available, true);
    assert.equal(
      (seen as { available: true; remainingGlobalStages: number }).remainingGlobalStages,
      32,
    );
    assert.equal(remainingGlobalStages(progress()), 32);
  });

  it('runs only the remaining work for a partially played season', () => {
    const partial = progress({ globalStage: 30 });
    assert.equal(remainingGlobalStages(partial), 3);
    const seen = fastForwardAvailability(partial, status({ globalStage: 30 }));
    assert.equal(seen.available, true);
    assert.equal(
      (seen as { available: true; remainingGlobalStages: number }).remainingGlobalStages,
      3,
    );
  });

  it('is disabled with an explanation once Stage 32 is persisted', () => {
    const done = progress({ globalStage: 33, isSeasonComplete: true });
    const seen = fastForwardAvailability(done, status());
    assert.equal(seen.available, false);
    assert.match((seen as { available: false; reason: string }).reason, /Stage 32/);
    assert.equal(remainingGlobalStages(done), 0);
  });

  it('is disabled during a postseason or next-season transition', () => {
    const postseason = status({
      computedPhase: 'SeasonComplete',
      isCurrentSeasonComplete: true,
      seasonComplete: true,
      sourceSeasonNumber: 1,
      nextSeasonNumber: 2,
      legalNextActions: ['ResolveInauguralMovement'],
    });
    const seen = fastForwardAvailability(progress({ globalStage: 33 }), postseason);
    assert.equal(seen.available, false);
    assert.match((seen as { available: false; reason: string }).reason, /postseason/i);
  });

  it('is disabled when the lifecycle has moved past league stages', () => {
    const moved = status({ legalNextActions: ['RunQualifier'] });
    const seen = fastForwardAvailability(progress(), moved);
    assert.equal(seen.available, false);
    assert.match((seen as { available: false; reason: string }).reason, /league stages/);
  });

  it('stays available when lifecycle status is temporarily unavailable', () => {
    const seen = fastForwardAvailability(progress(), null);
    assert.equal(seen.available, true);
  });
});

describe('MSS-042 fast-forward copy', () => {
  it('confirms the season, remaining stages, league scope and no-undo terms', () => {
    const text = fastForwardConfirmationText(progress(), 32);
    assert.match(text, /Season 1/);
    assert.match(text, /32 remaining/);
    assert.match(text, /2 active leagues/);
    assert.match(text, /before movement, qualifiers and Cups/);
    assert.match(text, /cannot be undone/);
  });
});

describe('MSS-042 fast-forward wiring', () => {
  it('exposes one complete-season client for the route-scoped save id', () => {
    assert.ok(api.includes('completeSeason'), 'dashboardApi exposes completeSeason');
    assert.ok(
      api.includes('/seasons/complete-season'),
      'fast-forward reuses POST complete-season',
    );
    assert.ok(
      !api.includes('saves/${saveId}/simulate-seasons'),
      'dashboardApi never calls the postseason batch endpoint',
    );
    assert.ok(api.includes('CompleteSeasonResult'), 'typed bulk response is shared');
  });

  it('labels the action, confirms deliberately, and guards duplicates', () => {
    assert.ok(component.includes('Fast-forward league season'), 'clear action label');
    assert.ok(component.includes('Confirm fast-forward'), 'deliberate confirmation');
    assert.ok(component.includes('Cancel'), 'confirmation can be cancelled');
    assert.ok(component.includes('if (running)'), 'duplicate submissions are blocked');
    assert.ok(
      component.includes('completeSeason(saveId)'),
      'calls the bulk operation once for the selected save id',
    );
    assert.ok(!component.includes('simulate'), 'never triggers the postseason batch endpoint');
  });

  it('shows busy, disabled, error and committed-transition states with refresh recovery', () => {
    assert.ok(component.includes('aria-busy'), 'unmistakable running state');
    assert.ok(component.includes('<progress'), 'indeterminate busy indicator');
    assert.ok(component.includes('disabled'), 'disabled states exist');
    assert.ok(component.includes('Refresh state'), 'failed runs offer authoritative refresh');
    assert.ok(component.includes('Retry fast-forward'), 'retry is offered while stages remain');
    assert.ok(component.includes('onCompleted()'), 'commit refreshes dashboard progress state');
    assert.ok(
      component.includes('Loading postseason status'),
      'commit shows a neutral loading transition',
    );
  });

  it('keeps postseason on the normal one-event-at-a-time lifecycle controls', () => {
    assert.ok(
      component.includes('stops before movement, qualifiers and Cups'),
      'postseason exclusion is explicit',
    );
    assert.ok(helpers.includes('CompleteNextGlobalStage'), 'postseason gate reuses lifecycle actions');
  });

  it('mounts the shortcut inside the Next step panel on the save dashboard', () => {
    assert.ok(dashboardPage.includes('FastForwardSeason'), 'dashboard renders the shortcut');
    assert.ok(
      dashboardPage.includes('title="Next step"') && dashboardPage.includes('<SeasonFlow'),
      'shortcut sits next to the season flow',
    );
    assert.ok(
      dashboardPage.includes('progress={progress}') && dashboardPage.includes('status={status}'),
      'shortcut receives live progress and lifecycle status',
    );
    assert.ok(
      dashboardPage.includes('saveId={saveId}'),
      'shortcut uses the route-scoped save id',
    );
  });

  it('offers a contextual entry point from Live without duplicating simulation', () => {
    assert.ok(
      livePage.includes('Fast-forward the league season on the Dashboard'),
      'live links to the dashboard shortcut',
    );
    assert.ok(!livePage.includes('completeSeason('), 'live keeps single-league controls only');
  });
});

describe('MSS-074 skip the transient league-complete screen', () => {
  it('removes the League season complete panel and its four-button action bar', () => {
    assert.ok(!component.includes('League season complete'), 'no intermediate completed-league title');
    assert.ok(!component.includes('Inspect final tables'), 'no Inspect final tables shortcut');
    assert.ok(!component.includes('Replay history'), 'no Replay history shortcut');
    assert.ok(!component.includes('Inspect Cups'), 'no Inspect Cups shortcut');
    assert.ok(!component.includes('Refresh status'), 'no Refresh status shortcut in the removed bar');
    assert.ok(!component.includes('Postseason next steps'), 'removed action-bar group is gone');
    assert.ok(!helpers.includes('summarizeCompleteSeason'), 'obsolete summary helper is gone');
    assert.ok(!helpers.includes('postFastForwardNextStep'), 'obsolete next-step helper is gone');
  });

  it('shows a neutral loading transition after the commit, without inventing postseason steps', () => {
    assert.ok(component.includes("'committed'"), 'commit phase is distinct from running');
    assert.ok(
      component.includes('loading postseason'),
      'committed state names the loading transition',
    );
    assert.ok(
      !component.includes('ResolveInauguralMovement') && !component.includes('ResolveAutomaticMovement'),
      'fast-forward never runs postseason actions itself',
    );
  });

  it('keeps final tables, history, Cups and refresh reachable outside the removed bar', () => {
    assert.ok(dashboardPage.includes('standingsPath(saveId)'), 'dashboard still links to standings');
    assert.ok(dashboardPage.includes('cupsPath(saveId)'), 'dashboard still links to Cups');
    assert.ok(shell.includes('historyPath'), 'global navigation still links to history');
    assert.ok(shell.includes('standingsPath'), 'global navigation still links to standings');
    assert.ok(shell.includes('cupsPath'), 'global navigation still links to cups');
    assert.ok(dashboardPage.includes('onClick={onRefresh}'), 'dashboard keeps its normal refresh control');
    assert.ok(dashboardPage.includes('Refresh'), 'dashboard refresh copy remains');
    assert.ok(seasonFlow.includes('Refresh') || seasonFlow.includes('Retry'), 'season flow keeps error retry');
  });

  it('drives the post-fast-forward transition from authoritative status, not local counters', () => {
    assert.ok(
      dashboardPage.includes('leagueCompletionPending'),
      'dashboard tracks the pending postseason refresh',
    );
    assert.ok(
      dashboardPage.includes('transitionPending={leagueCompletionPending}'),
      'season flow receives the pending transition flag',
    );
    assert.ok(
      seasonFlow.includes('transitionPending'),
      'season flow supports a pending-transition loading state',
    );
    assert.ok(
      seasonFlow.includes('Loading postseason status'),
      'pending transition prefers loading over stale league content',
    );
  });
});
