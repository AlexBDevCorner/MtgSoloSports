import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  fastForwardAvailability,
  fastForwardConfirmationText,
  postFastForwardNextStep,
  remainingGlobalStages,
  summarizeCompleteSeason,
} from './fastForwardSeason.ts';
import type {
  CompleteSeasonResult,
  SeasonProgress,
  SeasonProgressLeague,
  SeasonStatus,
} from './dashboardApi.ts';

const here = dirname(fileURLToPath(import.meta.url));
const component = readFileSync(join(here, 'FastForwardSeason.tsx'), 'utf8');
const helpers = readFileSync(join(here, 'fastForwardSeason.ts'), 'utf8');
const api = readFileSync(join(here, 'dashboardApi.ts'), 'utf8');
const dashboardPage = readFileSync(join(here, 'DashboardPage.tsx'), 'utf8');
const livePage = readFileSync(join(here, '..', 'live', 'LivePage.tsx'), 'utf8');

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
    ...overrides,
  };
}

function completedResult(seasonNumber: number): CompleteSeasonResult {
  return {
    saveId: 'save-1',
    seasonNumber,
    stagesCompleted: 32,
    globalStageBefore: 1,
    globalStageAfter: 33,
    isSeasonComplete: true,
    rngBeforeState: 1,
    rngBeforeStream: 2,
    rngAfterState: 3,
    rngAfterStream: 4,
    progress: {
      stagesCompleted: 32,
      totalStagesInSeason: 32,
      globalStageBefore: 1,
      globalStageAfter: 33,
    },
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

  it('summarizes success from real response counts and cursors', () => {
    const summary = summarizeCompleteSeason(completedResult(2));
    assert.match(summary, /Season 2/);
    assert.match(summary, /32/);
    assert.match(summary, /1 → 33/);
  });

  it('hands Season 1 to the inaugural Superleague and later seasons to movement/qualifier/Cups', () => {
    assert.match(postFastForwardNextStep(completedResult(1)), /inaugural Superleague/);
    assert.match(
      postFastForwardNextStep(completedResult(2)),
      /movement → qualifier → feeder rebalancing/,
    );
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

  it('shows busy, disabled, error and success states with refresh recovery', () => {
    assert.ok(component.includes('aria-busy'), 'unmistakable running state');
    assert.ok(component.includes('<progress'), 'indeterminate busy indicator');
    assert.ok(component.includes('disabled'), 'disabled states exist');
    assert.ok(component.includes('Refresh state'), 'failed runs offer authoritative refresh');
    assert.ok(component.includes('Retry fast-forward'), 'retry is offered while stages remain');
    assert.ok(
      component.includes('summarizeCompleteSeason(result)'),
      'success uses real response counts/cursors',
    );
    assert.ok(component.includes('onCompleted()'), 'success refreshes dashboard progress state');
  });

  it('keeps postseason inspectable one event at a time', () => {
    assert.ok(
      component.includes('stops before movement, qualifiers and Cups') ||
        component.includes('No postseason action has run'),
      'postseason exclusion is explicit',
    );
    assert.ok(component.includes('standingsPath(saveId)'), 'links to final league tables');
    assert.ok(component.includes('historyPath(saveId)'), 'links to persisted history');
    assert.ok(component.includes('cupsPath(saveId)'), 'links to Cup inspection');
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
      dashboardPage.includes('saveId={saveId}') && dashboardPage.includes('onCompleted={onRefresh}'),
      'shortcut uses the route-scoped save id and refreshes dashboard state',
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
