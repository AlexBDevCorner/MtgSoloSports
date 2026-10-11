import { describe, it, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { create, act } from 'react-test-renderer';
import { SeasonFlow } from './SeasonFlow.tsx';
import { DashboardPage } from './DashboardPage.tsx';
import { seasonFlow } from './seasonFlowSteps.ts';
import type { SeasonProgress, SeasonStatus } from './dashboardApi.ts';
import type { DashboardData } from './useDashboard.ts';

// MSS-074: fast-forwarding the remaining league season must move directly to
// Promotion & relegation without flashing the removed "League season complete"
// panel (and its four-button bar). The next step comes from authoritative
// lifecycle status; while the postseason refresh is pending the UI prefers a
// stable loading state over stale League-play content.

(globalThis as unknown as { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
(globalThis as unknown as { React: typeof React }).React = React;

const SAVE = '33333333-3333-3333-3333-333333333333';

function okJson(data: unknown): unknown {
  return {
    ok: true,
    status: 200,
    statusText: 'OK',
    json: () => Promise.resolve(data),
    text: () => Promise.resolve(JSON.stringify(data)),
  };
}

function installGlobals(post: (url: string) => unknown): void {
  const g = globalThis as unknown as Record<string, unknown>;
  if (!g['localStorage']) {
    const store = new Map<string, string>();
    g['localStorage'] = {
      getItem: (k: string) => (store.has(k) ? (store.get(k) as string) : null),
      setItem: (k: string, v: string) => {
        store.set(k, v);
      },
      removeItem: (k: string) => {
        store.delete(k);
      },
      clear: () => {
        store.clear();
      },
    };
  } else {
    (g['localStorage'] as { clear: () => void }).clear();
  }
  if (!g['window']) {
    (g['window'] as unknown) = g;
  }
  const w = g['window'] as unknown as Record<string, unknown>;
  w['matchMedia'] = () => ({
    matches: false,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
  });
  w['history'] = { pushState: () => undefined, replaceState: () => undefined };
  w['dispatchEvent'] = () => true;
  w['addEventListener'] = () => undefined;
  w['removeEventListener'] = () => undefined;
  g['fetch'] = (input: unknown, init?: { method?: string }) => {
    const url = String(input);
    const method = (init?.method ?? 'GET').toUpperCase();
    if (method !== 'GET') {
      return Promise.resolve(post(url));
    }
    return Promise.resolve(okJson({}));
  };
}

function leagueRunningProgress(): SeasonProgress {
  return {
    saveId: SAVE,
    seasonNumber: 2,
    globalStage: 32,
    isSeasonComplete: false,
    leagues: [
      { leagueId: 1, leagueName: 'Superleague', leagueKind: 'Superleague', currentStage: 32, completedStages: 31, isLeagueComplete: false },
    ],
  };
}

function leagueRunningStatus(): SeasonStatus {
  return {
    saveId: SAVE,
    currentSeasonNumber: 2,
    persistedPhase: 'SeasonInProgress',
    computedPhase: 'SeasonInProgress',
    sourceSeasonNumber: null,
    nextSeasonNumber: null,
    isInauguralTransition: false,
    globalStage: 32,
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
  };
}

function movementPendingStatus(): SeasonStatus {
  return {
    saveId: SAVE,
    currentSeasonNumber: 2,
    persistedPhase: 'SeasonComplete',
    computedPhase: 'SeasonComplete',
    sourceSeasonNumber: 2,
    nextSeasonNumber: 3,
    isInauguralTransition: false,
    globalStage: 33,
    isCurrentSeasonComplete: true,
    seasonComplete: true,
    movementResolved: false,
    qualifierResolved: false,
    rebalanced: false,
    cupSelectionResolved: false,
    cupIndividualResolved: false,
    cupTeamResolved: false,
    cupComplete: false,
    readyToStartNextSeason: false,
    expectedCup: 'ColorCup',
    legalNextActions: ['ResolveAutomaticMovement'],
    nextActionDetail: 'Resolve promotion and relegation.',
    eventProgress: null,
  };
}

function completeProgress(): SeasonProgress {
  return {
    saveId: SAVE,
    seasonNumber: 2,
    globalStage: 33,
    isSeasonComplete: true,
    leagues: [
      { leagueId: 1, leagueName: 'Superleague', leagueKind: 'Superleague', currentStage: null, completedStages: 32, isLeagueComplete: true },
    ],
  };
}

function dashboardData(
  progress: SeasonProgress,
  status: SeasonStatus | null,
  statusError: string | null = null,
): DashboardData {
  return {
    detail: {
      saveId: SAVE,
      name: 'Test save',
      createdUtc: '2026-01-01T00:00:00Z',
      schemaVersion: 1,
      currentSeason: 2,
      phase: 'SeasonInProgress',
      rngAlgorithm: 'test',
      rngVersion: 1,
      rngState: '0',
      rngStream: '0',
      rulesVersion: 1,
    },
    progress,
    rosters: null,
    status,
    statusError,
    stories: [],
    leaders: [],
    superleagueComposition: [],
    recentHonours: [],
    records: null,
    hallOfFame: [],
  };
}

async function flush(rounds = 8): Promise<void> {
  for (let i = 0; i < rounds; i += 1) {
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
  }
}

function textOf(renderer: ReturnType<typeof create>): string {
  return JSON.stringify((renderer as unknown as { toJSON: () => unknown }).toJSON());
}

type TestInstance = {
  props: Record<string, unknown>;
  findAllByType: (t: string) => TestInstance[];
};

function rootOf(renderer: ReturnType<typeof create>): TestInstance {
  return (renderer as unknown as { root: TestInstance }).root;
}

function findButtonByText(renderer: ReturnType<typeof create>, text: string): Record<string, unknown> {
  const buttons = rootOf(renderer).findAllByType('button');
  for (const button of buttons) {
    const children = button.props['children'];
    const flat = Array.isArray(children) ? children.join('') : String(children ?? '');
    if (flat === text) {
      return button.props;
    }
  }
  throw new Error(`button with text ${JSON.stringify(text)} not found`);
}

function hasButtonWithText(renderer: ReturnType<typeof create>, text: string): boolean {
  try {
    findButtonByText(renderer, text);
    return true;
  } catch {
    return false;
  }
}

describe('MSS-074 authoritative end-of-league transition', () => {
  beforeEach(() => {
    installGlobals(() => okJson({}));
  });

  it('prefers fresh lifecycle status over a stale local stage counter', () => {
    // Stale progress still claims league play is running, but the authoritative
    // status already confirms completion with movement pending.
    const stale = leagueRunningProgress();
    const fresh = movementPendingStatus();
    const flow = seasonFlow(stale, fresh);
    assert.equal(flow.steps[0]?.state, 'done');
    assert.equal(flow.steps[1]?.label, 'Promotion & relegation');
    assert.equal(flow.steps[1]?.state, 'current');
    assert.equal(flow.next?.kind, 'event');
    assert.equal(flow.next?.kind === 'event' ? flow.next.action : null, 'ResolveAutomaticMovement');
  });

  it('keeps league play current while the season genuinely runs', () => {
    const flow = seasonFlow(leagueRunningProgress(), leagueRunningStatus());
    assert.equal(flow.steps[0]?.state, 'current');
    assert.equal(flow.next?.kind, 'live');
  });

  it('keeps later postseason steps unaffected once movement resolves', () => {
    const flow = seasonFlow(
      completeProgress(),
      {
        ...movementPendingStatus(),
        movementResolved: true,
        legalNextActions: ['RunQualifier'],
        eventProgress: {
          event: 'qualifier',
          sourceSeasonNumber: 2,
          roundsPlayed: 0,
          totalRounds: 16,
          groupCount: 1,
          roundsPerGroup: 16,
          group: null,
          roundInGroup: null,
          completedEvents: 0,
          totalEvents: 1,
        },
      },
    );
    assert.equal(flow.steps[1]?.state, 'done');
    assert.equal(flow.steps.find((s) => s.key === 'qualifier')?.state, 'current');
  });

  it('shows a stable loading state while the postseason refresh is pending', async () => {
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(
        React.createElement(SeasonFlow, {
          saveId: SAVE,
          progress: leagueRunningProgress(),
          status: leagueRunningStatus(),
          onAdvanced: () => undefined,
          transitionPending: true,
        }),
      );
    });
    const text = textOf(renderer!);
    assert.ok(text.includes('Loading postseason status'), 'pending shows loading');
    assert.ok(!text.includes('League season complete'), 'no obsolete completed-league panel');
    assert.ok(!text.includes('Inspect final tables'), 'no removed action bar');
    assert.ok(!text.includes('Replay history'), 'no removed action bar');
    assert.ok(!text.includes('Inspect Cups'), 'no removed action bar');
    // The stale League-play step list is replaced by loading, never active.
    assert.ok(!text.includes('Play stage 32'), 'stale league-play action is hidden while pending');
  });

  it('moves directly to Promotion & relegation once fresh status arrives', async () => {
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(
        React.createElement(SeasonFlow, {
          saveId: SAVE,
          progress: completeProgress(),
          status: movementPendingStatus(),
          onAdvanced: () => undefined,
        }),
      );
    });
    const text = textOf(renderer!);
    assert.ok(text.includes('Promotion & relegation'), 'movement is the next step');
    assert.ok(!text.includes('League season complete'), 'no intermediate panel');
    assert.ok(!text.includes('Inspect final tables'), 'no removed four-button bar');
  });

  it('surfaces a failed status refresh as retry, never as completion', async () => {
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(
        React.createElement(SeasonFlow, {
          saveId: SAVE,
          progress: completeProgress(),
          status: null,
          statusError: 'Request failed (500).',
          onAdvanced: () => undefined,
        }),
      );
    });
    const text = textOf(renderer!);
    assert.ok(!text.includes('Nothing left to run for this season'), 'failure is not completion');
    assert.ok(text.includes('Season status unavailable'), 'failure is recoverable');
    findButtonByText(renderer!, 'Retry');
  });

  it('keeps pending loading with retry when the postseason refresh fails', async () => {
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(
        React.createElement(SeasonFlow, {
          saveId: SAVE,
          progress: leagueRunningProgress(),
          status: leagueRunningStatus(),
          statusError: 'Request failed (500).',
          refreshError: null,
          onAdvanced: () => undefined,
          transitionPending: true,
        }),
      );
    });
    const text = textOf(renderer!);
    assert.ok(text.includes('Loading postseason status'), 'pending keeps loading on failure');
    assert.ok(text.includes('Postseason refresh failed'), 'pending failure offers retry');
    findButtonByText(renderer!, 'Retry');
    assert.ok(!text.includes('Play stage 32'), 'stale league action stays hidden on failure');
    assert.ok(!text.includes('League season complete'), 'no obsolete panel on failure');
  });
});

describe('MSS-074 dashboard fast-forward skips the transient screen', () => {
  beforeEach(() => {
    installGlobals((url) => {
      if (url.endsWith('/seasons/complete-season')) {
        return okJson({
          saveId: SAVE,
          seasonNumber: 2,
          stagesCompleted: 1,
          globalStageBefore: 32,
          globalStageAfter: 33,
          isSeasonComplete: true,
          rngBeforeState: 1,
          rngBeforeStream: 1,
          rngAfterState: 2,
          rngAfterStream: 1,
          progress: {
            stagesCompleted: 1,
            totalStagesInSeason: 32,
            globalStageBefore: 32,
            globalStageAfter: 33,
          },
        });
      }
      return okJson({});
    });
  });

  it('fast-forwards to Promotion & relegation without the four-button bar, even on slow refresh', async () => {
    let refreshed = 0;
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(
        React.createElement(DashboardPage, {
          saveId: SAVE,
          data: dashboardData(leagueRunningProgress(), leagueRunningStatus()),
          loading: false,
          error: null,
          notFound: false,
          onRefresh: () => {
            refreshed += 1;
          },
        }),
      );
    });
    await flush();

    // Start from genuine league play: the fast-forward entry point is visible.
    assert.ok(hasButtonWithText(renderer!, 'Fast-forward league season'), 'entry point is offered');
    assert.ok(textOf(renderer!).includes('League play'), 'league play is current before completion');

    // Open the deliberate confirmation, then confirm the single bulk operation.
    await act(async () => {
      (findButtonByText(renderer!, 'Fast-forward league season')['onClick'] as () => void)();
    });
    await flush();
    await act(async () => {
      (findButtonByText(renderer!, 'Confirm fast-forward')['onClick'] as () => void)();
    });
    await flush(12);

    // The bulk operation committed and requested a refresh. While the fresh
    // postseason status is still pending (slow refresh: props are still stale),
    // the dashboard must show loading — never the removed intermediate panel.
    assert.equal(refreshed, 1);
    const pendingText = textOf(renderer!);
    assert.ok(pendingText.includes('Loading postseason status'), 'slow refresh shows loading');
    assert.ok(!pendingText.includes('League season complete'), 'obsolete panel never flashes');
    assert.ok(!pendingText.includes('Inspect final tables'), 'removed bar never flashes');
    assert.ok(!pendingText.includes('Replay history'), 'removed bar never flashes');
    assert.ok(!pendingText.includes('Inspect Cups'), 'removed bar never flashes');
    assert.ok(!pendingText.includes('Refresh status'), 'removed bar never flashes');

    // The fresh authoritative status arrives: league done, movement pending.
    // The dashboard settles directly on Promotion & relegation.
    await act(async () => {
      renderer!.update(
        React.createElement(DashboardPage, {
          saveId: SAVE,
          data: dashboardData(completeProgress(), movementPendingStatus()),
          loading: false,
          error: null,
          notFound: false,
          onRefresh: () => {
            refreshed += 1;
          },
        }),
      );
    });
    await flush();
    const settledText = textOf(renderer!);
    assert.ok(settledText.includes('Promotion & relegation'), 'next legal step is shown');
    assert.ok(!settledText.includes('League season complete'), 'no intermediate panel after settle');
    assert.ok(!settledText.includes('Inspect final tables'), 'no removed bar after settle');
    assert.ok(!hasButtonWithText(renderer!, 'Fast-forward league season'), 'shortcut hides once complete');
  });

  it('keeps promotion playable and does not auto-run it', async () => {
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(
        React.createElement(DashboardPage, {
          saveId: SAVE,
          data: dashboardData(completeProgress(), movementPendingStatus()),
          loading: false,
          error: null,
          notFound: false,
          onRefresh: () => undefined,
        }),
      );
    });
    await flush();
    const text = textOf(renderer!);
    assert.ok(text.includes('Promotion & relegation'), 'movement is pending');
    assert.ok(text.includes('on Live'), 'movement offers its Live entry point');
    assert.ok(!text.includes('League season complete'), 'no auto-run claim via intermediate panel');
  });

  it('keeps the pending transition on a failed refresh instead of restoring stale league controls', async () => {
    let refreshed = 0;
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(
        React.createElement(DashboardPage, {
          saveId: SAVE,
          data: dashboardData(leagueRunningProgress(), leagueRunningStatus()),
          loading: false,
          error: null,
          notFound: false,
          onRefresh: () => {
            refreshed += 1;
          },
        }),
      );
    });
    await flush();
    await act(async () => {
      (findButtonByText(renderer!, 'Fast-forward league season')['onClick'] as () => void)();
    });
    await flush();
    await act(async () => {
      (findButtonByText(renderer!, 'Confirm fast-forward')['onClick'] as () => void)();
    });
    await flush(12);
    assert.equal(refreshed, 1);

    // The refresh fails at the top level: useDashboard keeps the stale
    // pre-completion snapshot and surfaces an error. The pending guard must
    // stay set so stale actionable League-play controls never return.
    await act(async () => {
      renderer!.update(
        React.createElement(DashboardPage, {
          saveId: SAVE,
          data: dashboardData(leagueRunningProgress(), leagueRunningStatus()),
          loading: false,
          error: 'Request failed (500).',
          notFound: false,
          onRefresh: () => {
            refreshed += 1;
          },
        }),
      );
    });
    await flush();
    const failedText = textOf(renderer!);
    assert.ok(failedText.includes('Loading postseason status'), 'failed refresh keeps loading');
    assert.ok(failedText.includes('Postseason refresh failed'), 'failed refresh offers retry');
    findButtonByText(renderer!, 'Retry');
    assert.ok(!failedText.includes('League season complete'), 'no obsolete panel on failure');
    assert.ok(
      !hasButtonWithText(renderer!, 'Fast-forward league season'),
      'stale fast-forward stays hidden on failure',
    );
    assert.ok(
      !failedText.includes('Complete stage 32 for all'),
      'stale actionable league control stays hidden on failure',
    );
  });
});

describe('MSS-074 manual final-stage completion enters the pending transition', () => {
  beforeEach(() => {
    installGlobals((url) => {
      if (url.endsWith('/advance-next-event')) {
        return okJson({
          saveId: SAVE,
          executedAction: 'CompleteNextGlobalStage',
          executedDetail: 'Completed global stage 32.',
          currentSeasonNumber: 2,
          nextSeasonNumber: 3,
          computedPhase: 'SeasonComplete',
          persistedPhase: 'SeasonComplete',
          sourceSeasonNumber: 2,
          isCurrentSeasonComplete: true,
          seasonComplete: true,
          movementResolved: false,
          qualifierResolved: false,
          rebalanced: false,
          cupSelectionResolved: false,
          cupIndividualResolved: false,
          cupTeamResolved: false,
          cupComplete: false,
          readyToStartNextSeason: false,
          expectedCup: 'ColorCup',
          legalNextActions: ['ResolveAutomaticMovement'],
          nextActionDetail: 'Resolve promotion and relegation.',
          eventProgress: null,
        });
      }
      return okJson({});
    });
  });

  it('routes a final manual stage through onLeagueCompleted, not onAdvanced', async () => {
    let advanced = 0;
    let leagueCompleted = 0;
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(
        React.createElement(SeasonFlow, {
          saveId: SAVE,
          progress: leagueRunningProgress(),
          status: leagueRunningStatus(),
          onAdvanced: () => {
            advanced += 1;
          },
          onLeagueCompleted: () => {
            leagueCompleted += 1;
          },
        }),
      );
    });
    await act(async () => {
      (findButtonByText(renderer!, 'Complete stage 32 for all 1 leagues')['onClick'] as () => void)();
    });
    await flush(12);
    assert.equal(leagueCompleted, 1, 'final stage notifies the pending-transition guard');
    assert.equal(advanced, 0, 'final stage does not take the ordinary refresh path');
  });

  it('keeps the ordinary refresh path for a non-final manual stage', async () => {
    installGlobals((url) => {
      if (String(url).endsWith('/advance-next-event')) {
        return okJson({
          saveId: SAVE,
          executedAction: 'CompleteNextGlobalStage',
          executedDetail: 'Completed global stage 5.',
          currentSeasonNumber: 2,
          nextSeasonNumber: null,
          computedPhase: 'SeasonInProgress',
          persistedPhase: 'SeasonInProgress',
          sourceSeasonNumber: null,
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
        });
      }
      return okJson({});
    });
    let advanced = 0;
    let leagueCompleted = 0;
    let renderer: ReturnType<typeof create> | null = null;
    const earlyProgress: SeasonProgress = {
      ...leagueRunningProgress(),
      globalStage: 5,
      leagues: [
        { leagueId: 1, leagueName: 'Superleague', leagueKind: 'Superleague', currentStage: 5, completedStages: 4, isLeagueComplete: false },
      ],
    };
    const earlyStatus: SeasonStatus = { ...leagueRunningStatus(), globalStage: 5 };
    await act(async () => {
      renderer = create(
        React.createElement(SeasonFlow, {
          saveId: SAVE,
          progress: earlyProgress,
          status: earlyStatus,
          onAdvanced: () => {
            advanced += 1;
          },
          onLeagueCompleted: () => {
            leagueCompleted += 1;
          },
        }),
      );
    });
    await act(async () => {
      (findButtonByText(renderer!, 'Complete stage 5 for all 1 leagues')['onClick'] as () => void)();
    });
    await flush(12);
    assert.equal(advanced, 1, 'non-final stage uses the ordinary refresh');
    assert.equal(leagueCompleted, 0, 'non-final stage does not enter the pending transition');
  });

  it('manually completing the last stage shows loading until authoritative movement arrives', async () => {
    let refreshed = 0;
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(
        React.createElement(DashboardPage, {
          saveId: SAVE,
          data: dashboardData(leagueRunningProgress(), leagueRunningStatus()),
          loading: false,
          error: null,
          notFound: false,
          onRefresh: () => {
            refreshed += 1;
          },
        }),
      );
    });
    await flush();
    await act(async () => {
      (findButtonByText(renderer!, 'Complete stage 32 for all 1 leagues')['onClick'] as () => void)();
    });
    await flush(12);
    assert.equal(refreshed, 1, 'manual completion refreshes authoritative status');
    const pendingText = textOf(renderer!);
    assert.ok(pendingText.includes('Loading postseason status'), 'slow refresh shows loading');
    assert.ok(!pendingText.includes('League season complete'), 'obsolete panel never flashes');
    assert.ok(
      !pendingText.includes('Complete stage 32 for all'),
      'stale enabled league control never returns while pending',
    );
    assert.ok(!hasButtonWithText(renderer!, 'Fast-forward league season'), 'stale fast-forward stays hidden');

    await act(async () => {
      renderer!.update(
        React.createElement(DashboardPage, {
          saveId: SAVE,
          data: dashboardData(completeProgress(), movementPendingStatus()),
          loading: false,
          error: null,
          notFound: false,
          onRefresh: () => {
            refreshed += 1;
          },
        }),
      );
    });
    await flush();
    const settledText = textOf(renderer!);
    assert.ok(settledText.includes('Promotion & relegation'), 'settles directly on movement');
    assert.ok(!settledText.includes('League season complete'), 'no intermediate panel after settle');
  });
});
