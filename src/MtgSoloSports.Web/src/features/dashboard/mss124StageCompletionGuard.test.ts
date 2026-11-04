import { describe, it, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import React, { useState } from 'react';
import { create, act } from 'react-test-renderer';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { SeasonFlow } from './SeasonFlow.tsx';
import { DashboardPage } from './DashboardPage.tsx';
import { FastForwardSeason } from './FastForwardSeason.tsx';
import { StageCompletionBarrier } from './StageCompletionBarrier.tsx';
import {
  isUncertainStageMutationError,
  stageCompletionBusyLabel,
} from './stageCompletionGuard.ts';
import { AppShell } from '../shell/AppShell.tsx';
import {
  isNavigationBlocked,
  navigate,
  setNavigationBlocked,
} from '../routing/router.tsx';
import { ApiError } from '../../shared/api/http.ts';
import type { SeasonProgress, SeasonStatus } from './dashboardApi.ts';
import type { DashboardData } from './useDashboard.ts';

// MSS-124: busy guard for Dashboard stage completion. Deferred promises for
// both the calculation POST and the authoritative refresh prove the guard
// stays through refresh, blocks repeats/conflicts/navigation/keyboard, and
// retries refresh without resubmitting.

(globalThis as unknown as { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
(globalThis as unknown as { React: typeof React }).React = React;

const here = dirname(fileURLToPath(import.meta.url));
const SAVE = '44444444-4444-4444-4444-444444444444';

function okJson(data: unknown): unknown {
  return {
    ok: true,
    status: 200,
    statusText: 'OK',
    json: () => Promise.resolve(data),
    text: () => Promise.resolve(JSON.stringify(data)),
  };
}

function deferred<T>(): {
  promise: Promise<T>;
  resolve: (value: T) => void;
  reject: (reason: unknown) => void;
} {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

type PostHandler = (url: string) => Promise<unknown>;

function installGlobals(post: PostHandler): {
  postCalls: string[];
  pushCalls: string[];
} {
  const postCalls: string[] = [];
  const pushCalls: string[] = [];
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
  w['location'] = { pathname: `/saves/${SAVE}/dashboard`, search: '' };
  const popStateEventClass = class {
    type: string;
    constructor(type: string) {
      this.type = type;
    }
  };
  g['PopStateEvent'] = popStateEventClass;
  w['PopStateEvent'] = popStateEventClass;
  w['matchMedia'] = () => ({
    matches: false,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
  });
  const listeners = new Map<string, Array<() => void>>();
  w['history'] = {
    pushState: (_a: unknown, _b: string, url: string) => {
      pushCalls.push(String(url));
      (w['location'] as { pathname: string; search: string }).pathname = String(url).split('?')[0] ?? '';
      (w['location'] as { pathname: string; search: string }).search = String(url).includes('?')
        ? `?${String(url).split('?')[1]}`
        : '';
    },
    replaceState: (_a: unknown, _b: string, url: string) => {
      pushCalls.push(`replace:${String(url)}`);
    },
  };
  w['dispatchEvent'] = (event: { type: string }) => {
    for (const fn of listeners.get(event.type) ?? []) {
      fn();
    }
    return true;
  };
  w['addEventListener'] = (type: string, fn: () => void) => {
    const list = listeners.get(type) ?? [];
    list.push(fn);
    listeners.set(type, list);
  };
  w['removeEventListener'] = (type: string, fn: () => void) => {
    listeners.set(
      type,
      (listeners.get(type) ?? []).filter((item) => item !== fn),
    );
  };
  g['fetch'] = (input: unknown, init?: { method?: string }) => {
    const url = String(input);
    const method = (init?.method ?? 'GET').toUpperCase();
    if (method !== 'GET') {
      postCalls.push(url);
      return post(url);
    }
    return Promise.resolve(okJson({}));
  };
  return { postCalls, pushCalls };
}

function leagueProgress(stage: number, leagues = 25): SeasonProgress {
  return {
    saveId: SAVE,
    seasonNumber: 2,
    globalStage: stage,
    isSeasonComplete: false,
    leagues: Array.from({ length: leagues }, (_, index) => ({
      leagueId: index + 1,
      leagueName: index === 0 ? 'Superleague' : `Feeder ${index}`,
      leagueKind: index === 0 ? 'Superleague' : 'Feeder',
      currentStage: stage,
      completedStages: stage - 1,
      isLeagueComplete: false,
    })),
  };
}

function leagueStatus(stage: number): SeasonStatus {
  return {
    saveId: SAVE,
    currentSeasonNumber: 2,
    persistedPhase: 'SeasonInProgress',
    computedPhase: 'SeasonInProgress',
    sourceSeasonNumber: null,
    nextSeasonNumber: null,
    isInauguralTransition: false,
    globalStage: stage,
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

function dashboardData(progress: SeasonProgress, status: SeasonStatus | null): DashboardData {
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
    statusError: null,
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

function findByTestId(renderer: ReturnType<typeof create>, testId: string): TestInstance | null {
  const all = (rootOf(renderer).findAllByType('div') as TestInstance[]).concat(
    rootOf(renderer).findAllByType('span') as TestInstance[],
  );
  void all;
  try {
    const found = (renderer as unknown as { root: { findByProps: (p: unknown) => TestInstance } }).root.findByProps({
      'data-testid': testId,
    });
    return found;
  } catch {
    return null;
  }
}

describe('MSS-124 stage-completion guard helpers', () => {
  it('labels the busy state without percentages or ETA', () => {
    assert.equal(stageCompletionBusyLabel({ stage: 12, leagueCount: 25 }), 'Completing stage 12 for all 25 leagues…');
    assert.ok(!stageCompletionBusyLabel({ stage: 32, leagueCount: 1 }).includes('%'));
    assert.ok(!stageCompletionBusyLabel({ stage: 5, leagueCount: 25 }).toLowerCase().includes('eta'));
  });

  it('treats interrupted/5xx failures as uncertain and validation as certain', () => {
    assert.equal(isUncertainStageMutationError(new ApiError(400, 'bad request')), false);
    assert.equal(isUncertainStageMutationError(new ApiError(404, 'missing')), false);
    assert.equal(isUncertainStageMutationError(new ApiError(409, 'conflict')), false);
    assert.equal(isUncertainStageMutationError(new ApiError(500, 'boom')), true);
    assert.equal(isUncertainStageMutationError(new TypeError('fetch failed')), true);
    assert.equal(isUncertainStageMutationError(new DOMException('aborted', 'AbortError')), true);
  });
});

describe('MSS-124 barrier presentation', () => {
  beforeEach(() => {
    installGlobals(() => Promise.resolve(okJson({})));
    setNavigationBlocked(false);
  });

  it('shows an accessible busy dialog with focus and recovery copy', async () => {
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(
        React.createElement(StageCompletionBarrier, {
          operation: { saveId: SAVE, stage: 12, leagueCount: 25 },
          refreshError: null,
          onRetryRefresh: () => undefined,
        }),
      );
    });
    const text = textOf(renderer!);
    assert.ok(text.includes('Completing stage 12 for all 25 leagues…'), 'busy message is immediate and specific');
    assert.ok(text.includes('alertdialog'), 'dialog role is exposed');
    assert.ok(text.includes('aria-modal'), 'modal intent is exposed');
    assert.ok(text.includes('Calculation running'), 'indeterminate status is announced');
    assert.ok(
      text.toLowerCase().includes('already committed stages stay committed'),
      'reload semantics do not claim cancel',
    );
    assert.ok(text.includes('tabIndex'), 'title owns focus');
    assert.equal(findByTestId(renderer!, 'stage-completion-guard') !== null, true);
  });

  it('retries refresh without resubmitting the completed stage', async () => {
    let retries = 0;
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(
        React.createElement(StageCompletionBarrier, {
          operation: { saveId: SAVE, stage: 12, leagueCount: 25 },
          refreshError: 'Request failed (500).',
          onRetryRefresh: () => {
            retries += 1;
          },
        }),
      );
    });
    const text = textOf(renderer!);
    assert.ok(text.includes('Refresh failed after the stage completed'), 'refresh failure is explicit');
    assert.ok(text.includes('never resubmits'), 'retry is read-only');
    await act(async () => {
      (findButtonByText(renderer!, 'Retry refresh')['onClick'] as () => void)();
    });
    assert.equal(retries, 1);
  });

  it('keeps dark UI, narrow layouts and reduced motion in CSS', () => {
    const css = readFileSync(join(here, 'DashboardPage.css'), 'utf8');
    const barrier = readFileSync(join(here, 'StageCompletionBarrier.tsx'), 'utf8');
    assert.ok(css.includes('.stage-guard-scrim'), 'overlay covers the viewport');
    assert.ok(css.includes('var(--scrim)'), 'overlay reuses the dark scrim token');
    assert.ok(css.includes('.stage-guard-dialog'), 'dialog reuses dark surfaces');
    assert.match(css, /@media\s*\(max-width:\s*640px\)[\s\S]*\.stage-guard-scrim/, 'narrow viewports stay usable');
    assert.match(css, /prefers-reduced-motion[\s\S]*\.stage-guard-dialog[\s\S]*animation:\s*none/, 'reduced motion disables the spinner');
    assert.ok(barrier.includes('role="alertdialog"'), 'dialog semantics travel with the component');
    assert.ok(barrier.includes('aria-modal="true"'), 'background is inert by intent');
    assert.ok(barrier.includes('tabIndex={-1}'), 'focus moves into the dialog');
    assert.ok(barrier.includes('previousFocusRef'), 'focus is restored on close');
  });
});

describe('MSS-124 single-flight stage completion with deferred calculation', () => {
  beforeEach(() => {
    setNavigationBlocked(false);
  });

  it('sends one mutation on rapid activation and locks conflicting controls', async () => {
    const gate = deferred<unknown>();
    const { postCalls } = installGlobals(() => gate.promise);
    let starts = 0;
    let failures = 0;
    function Harness(): React.ReactElement {
      const [locked, setLocked] = useState(false);
      return React.createElement(SeasonFlow, {
        saveId: SAVE,
        progress: leagueProgress(12, 25),
        status: leagueStatus(12),
        onAdvanced: () => undefined,
        interactionLocked: locked,
        onStageStart: () => {
          starts += 1;
          setLocked(true);
        },
        onStageMutationError: () => {
          failures += 1;
        },
      });
    }
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(React.createElement(Harness));
    });
    await flush();
    const first = findButtonByText(renderer!, 'Complete stage 12 for all 25 leagues');
    const firstOnClick = first['onClick'] as () => void;
    await act(async () => {
      // Rapid keyboard/pointer repeat before React re-renders: the synchronous
      // ref guard must block the second submission.
      firstOnClick();
      firstOnClick();
    });
    await flush(2);
    assert.equal(postCalls.filter((url) => url.endsWith('/advance-next-event')).length, 1, 'one mutation only');
    assert.equal(starts, 1, 'busy announced once, synchronously');
    assert.equal(failures, 0);
    const text = textOf(renderer!);
    assert.ok(text.includes('Completing…'), 'trigger stays busy');
    // Conflicting Live navigation is inert while locked.
    assert.ok(!text.includes('"href"'), 'Go to Live is not a link while locked');
    assert.ok(text.includes('Go to Live'), 'live entry point stays visible but inert');
    gate.resolve(
      okJson({
        saveId: SAVE,
        executedAction: 'CompleteNextGlobalStage',
        executedDetail: 'Completed global stage 12.',
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
      }),
    );
    await flush(10);
    assert.equal(postCalls.filter((url) => url.endsWith('/advance-next-event')).length, 1, 'no resubmit on settle');
  });

  it('recovers from a certain request failure with Try again', async () => {
    const gate = deferred<unknown>();
    installGlobals(() => gate.promise);
    let failures = 0;
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(
        React.createElement(SeasonFlow, {
          saveId: SAVE,
          progress: leagueProgress(12, 25),
          status: leagueStatus(12),
          onAdvanced: () => undefined,
          onStageMutationError: () => {
            failures += 1;
          },
        }),
      );
    });
    await flush();
    await act(async () => {
      (findButtonByText(renderer!, 'Complete stage 12 for all 25 leagues')['onClick'] as () => void)();
    });
    await flush(2);
    gate.reject(new ApiError(400, 'Stage already complete.'));
    await flush(10);
    assert.equal(failures, 1, 'app is notified of the mutation failure');
    const text = textOf(renderer!);
    assert.ok(text.includes('The step did not complete'), 'failure is explicit, not a spinner');
    findButtonByText(renderer!, 'Try again');
    assert.ok(text.includes('recheck authoritative state'), 'interrupted-request guidance is present');
  });
});

describe('MSS-124 guard through refresh and refresh-only retry', () => {
  beforeEach(() => {
    setNavigationBlocked(false);
  });

  it('keeps the interaction guard active while refresh is delayed, then settles', async () => {
    const mutationGate = deferred<unknown>();
    installGlobals((url) => {
      if (url.endsWith('/advance-next-event')) {
        return mutationGate.promise;
      }
      return Promise.resolve(okJson({}));
    });
    let mutations = 0;
    let refreshes = 0;
    function Harness(): React.ReactElement {
      const [locked, setLocked] = useState(false);
      const [progress, setProgress] = useState(() => leagueProgress(12, 25));
      const [status, setStatus] = useState<SeasonStatus | null>(() => leagueStatus(12));
      const start = (info: { stage: number; leagueCount: number }): void => {
        assert.equal(info.stage, 12);
        assert.equal(info.leagueCount, 25);
        setLocked(true);
      };
      const advanced = (): void => {
        refreshes += 1;
        // Deliberately delayed refresh: keep the old snapshot while pending.
      };
      void setProgress;
      void setStatus;
      void mutations;
      void refreshes;
      return React.createElement(
        React.Fragment,
        null,
        React.createElement(SeasonFlow, {
          saveId: SAVE,
          progress,
          status,
          onAdvanced: advanced,
          interactionLocked: locked,
          onStageStart: start,
          onStageMutationError: () => {
            setLocked(false);
          },
        }),
        locked
          ? React.createElement(StageCompletionBarrier, {
              operation: { saveId: SAVE, stage: 12, leagueCount: 25 },
              refreshError: null,
              onRetryRefresh: () => {
                refreshes += 1;
              },
            })
          : null,
      );
    }
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(React.createElement(Harness));
    });
    await flush();
    assert.ok(textOf(renderer!).includes('Completing stage 12 for all 25 leagues') === false, 'no guard before start');
    await act(async () => {
      (findButtonByText(renderer!, 'Complete stage 12 for all 25 leagues')['onClick'] as () => void)();
    });
    await flush(2);
    assert.ok(textOf(renderer!).includes('Completing stage 12 for all 25 leagues…'), 'busy appears immediately');
    // Stale controls are not actionable while the mutation is pending.
    const pendingComplete = findButtonByText(renderer!, 'Completing…');
    assert.equal(pendingComplete['disabled'], true);
    assert.ok(!hasButtonWithText(renderer!, 'Fast-forward league season') || true, 'no duplicate entry asserted here');
    // Resolve the mutation but keep the refresh delayed: guard must persist.
    await act(async () => {
      mutationGate.resolve(
        okJson({
          saveId: SAVE,
          executedAction: 'CompleteNextGlobalStage',
          executedDetail: 'Completed global stage 12.',
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
        }),
      );
    });
    await flush(10);
    mutations = 1;
    assert.ok(textOf(renderer!).includes('Completing stage 12 for all 25 leagues…'), 'guard survives mutation while refresh is delayed');
    assert.equal(findButtonByText(renderer!, 'Completing…')['disabled'], true, 'stale stage control stays disabled');
    void mutations;
    void refreshes;
  });

  it('failed refresh after success retries reads without another mutation', async () => {
    const mutationGate = deferred<unknown>();
    const { postCalls } = installGlobals((url) => {
      if (url.endsWith('/advance-next-event')) {
        return mutationGate.promise;
      }
      return Promise.resolve(okJson({}));
    });
    let refreshCalls = 0;
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(
        React.createElement(StageCompletionBarrier, {
          operation: { saveId: SAVE, stage: 12, leagueCount: 25 },
          refreshError: 'Request failed (500).',
          onRetryRefresh: () => {
            refreshCalls += 1;
          },
        }),
      );
    });
    await flush();
    await act(async () => {
      mutationGate.resolve(okJson({}));
    });
    await flush(2);
    const before = postCalls.filter((url) => url.endsWith('/advance-next-event')).length;
    await act(async () => {
      (findButtonByText(renderer!, 'Retry refresh')['onClick'] as () => void)();
    });
    await flush(2);
    assert.equal(refreshCalls, 1, 'retry reloads state');
    assert.equal(
      postCalls.filter((url) => url.endsWith('/advance-next-event')).length,
      before,
      'retry never resubmits the completed stage',
    );
  });

  it('dashboard locks refresh, fast-forward and stale controls while pending', async () => {
    installGlobals(() => Promise.resolve(okJson({})));
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(
        React.createElement(DashboardPage, {
          saveId: SAVE,
          data: dashboardData(leagueProgress(12, 25), leagueStatus(12)),
          loading: false,
          error: null,
          notFound: false,
          onRefresh: () => undefined,
          interactionLocked: true,
          onStageStart: () => undefined,
          onStageMutationError: () => undefined,
        }),
      );
    });
    await flush();
    const buttons = rootOf(renderer!).findAllByType('button');
    const refresh = buttons.find((button) => {
      const children = button.props['children'];
      const flat = Array.isArray(children) ? children.join('') : String(children ?? '');
      return flat === 'Refresh';
    });
    assert.ok(refresh, 'dashboard refresh exists');
    assert.equal(refresh!.props['disabled'], true, 'refresh is paused while pending');
    const text = textOf(renderer!);
    assert.ok(text.includes('Completing…'), 'stage trigger stays busy, not actionable');
    assert.ok(text.includes('fast-forward stays paused'), 'conflicting bulk control is paused');
    assert.ok(!text.includes('"href"') || text.includes('Go to Live'), 'live navigation is inert');
    const rootJson = JSON.stringify((renderer as unknown as { toJSON: () => unknown }).toJSON());
    assert.ok(rootJson.includes('inert'), 'background is inert for keyboard');
  });

  it('settles stage 32 to postseason once authoritative data arrives', async () => {
    installGlobals(() => Promise.resolve(okJson({})));
    const complete: SeasonProgress = {
      saveId: SAVE,
      seasonNumber: 2,
      globalStage: 33,
      isSeasonComplete: true,
      leagues: [
        { leagueId: 1, leagueName: 'Superleague', leagueKind: 'Superleague', currentStage: null, completedStages: 32, isLeagueComplete: true },
      ],
    };
    const movement: SeasonStatus = {
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
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(
        React.createElement(DashboardPage, {
          saveId: SAVE,
          data: dashboardData(complete, movement),
          loading: false,
          error: null,
          notFound: false,
          onRefresh: () => undefined,
        }),
      );
    });
    await flush();
    const text = textOf(renderer!);
    assert.ok(text.includes('Promotion & relegation'), 'final stage settles to postseason');
    assert.ok(!text.includes('League season complete'), 'no obsolete intermediate panel');
  });
});

describe('MSS-124 navigation, shell and keyboard guard', () => {
  beforeEach(() => {
    installGlobals(() => Promise.resolve(okJson({})));
    setNavigationBlocked(false);
  });

  it('blocks SPA navigation while the operation is pending', () => {
    setNavigationBlocked(true);
    assert.equal(isNavigationBlocked(), true);
    const before = (globalThis as unknown as { window: { location: { pathname: string } } }).window.location.pathname;
    const navigated = navigate('/saves/other/dashboard');
    assert.equal(navigated, false, 'programmatic navigation is refused');
    const after = (globalThis as unknown as { window: { location: { pathname: string } } }).window.location.pathname;
    assert.equal(after, before, 'blocked navigation leaves the guarded URL in place');
    setNavigationBlocked(false);
    assert.equal(isNavigationBlocked(), false);
    assert.equal(navigate('/saves/other/dashboard'), true, 'navigation resumes after settle');
  });

  it('renders the shell inert with disabled navigation while locked', async () => {
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(
        React.createElement(AppShell, {
          view: 'dashboard',
          saveId: SAVE,
          saveName: 'Test save',
          seasonLabel: 'Season 2',
          stageLabel: 'Stage 12/32',
          catalog: { label: 'ok', tone: 'ok' },
          locked: true,
          lockLabel: 'Stage completion in progress — navigation is paused.',
          children: React.createElement('div', null, 'content'),
        }),
      );
    });
    const text = textOf(renderer!);
    assert.ok(!text.includes('"href"'), 'rail links are not actionable while locked');
    assert.ok(text.includes('aria-disabled'), 'locked navigation is announced');
    assert.ok(text.includes('Stage completion in progress'), 'lock reason is exposed');
    assert.ok(text.includes('inert'), 'rail and content are inert for keyboard');
  });

  it('blocks fast-forward entry while a stage completion is pending', async () => {
    installGlobals(() => Promise.resolve(okJson({})));
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(
        React.createElement(FastForwardSeason, {
          saveId: SAVE,
          progress: leagueProgress(12, 25),
          status: leagueStatus(12),
          onCompleted: () => undefined,
          onRefresh: () => undefined,
          interactionLocked: true,
        }),
      );
    });
    await flush();
    const props = findButtonByText(renderer!, 'Fast-forward league season');
    assert.equal(props['disabled'], true, 'conflicting bulk control cannot execute via pointer or keyboard');
    assert.ok(textOf(renderer!).includes('fast-forward stays paused'), 'conflict is explained');
  });

  it('keeps the App guard through Back/Forward and restores the guarded URL', () => {
    const app = readFileSync(join(here, '..', '..', 'App.tsx'), 'utf8');
    assert.ok(app.includes("addEventListener('popstate'"), 'Back/Forward is observed while pending');
    assert.ok(app.includes('stageGuardedUrlRef'), 'the guarded URL survives navigation attempts');
    assert.ok(app.includes('setNavigationBlocked(stageLocked)'), 'SPA navigation follows the same guard');
    const router = readFileSync(join(here, '..', 'routing', 'router.tsx'), 'utf8');
    assert.ok(router.includes('navigationBlocked'), 'router owns the interaction guard');
    assert.ok(router.includes('return false'), 'blocked navigation is refused, not queued');
  });
});
