import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  isTransitionKey,
  TRANSITION_TITLES,
  transitionForAction,
} from '../events/eventModel.ts';
import { livePath, parseRoute } from '../routing/routes.ts';

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = join(here, '..', '..');
const SAVE = '11111111-1111-1111-1111-111111111111';

function read(rel: string): string {
  return readFileSync(join(webRoot, rel), 'utf8');
}

describe('MSS-053 transition keys', () => {
  it('recognises exactly the movement and rebalance transition keys', () => {
    assert.equal(isTransitionKey('movement'), true);
    assert.equal(isTransitionKey('rebalance'), true);
    assert.equal(isTransitionKey('qualifier'), false);
    assert.equal(isTransitionKey('color-cup-selection'), false);
    assert.equal(isTransitionKey('bogus'), false);
    assert.equal(isTransitionKey(null), false);
    assert.equal(isTransitionKey('toString'), false);
  });

  it('titles both transitions without pretending they are round competitions', () => {
    assert.equal(TRANSITION_TITLES.movement, 'Promotion & relegation');
    assert.equal(TRANSITION_TITLES.rebalance, 'Feeder rebalance');
  });

  it('maps lifecycle actions to their transition reveal', () => {
    assert.equal(transitionForAction('ResolveInauguralMovement'), 'movement');
    assert.equal(transitionForAction('ResolveAutomaticMovement'), 'movement');
    assert.equal(transitionForAction('RebalanceFeeders'), 'rebalance');
    assert.equal(transitionForAction('RunQualifier'), null);
    assert.equal(transitionForAction('SelectColorCup'), null);
    assert.equal(transitionForAction('SelectTypeCup'), null);
    assert.equal(transitionForAction('CompleteNextGlobalStage'), null);
    assert.equal(transitionForAction('StartNextSeason'), null);
    assert.equal(transitionForAction(null), null);
  });

  it('round-trips transition reveals through the Live route', () => {
    assert.equal(
      livePath(SAVE, { transition: 'movement', season: 2 }),
      `/saves/${SAVE}/live?transition=movement&season=2`,
    );
    assert.equal(
      livePath(SAVE, { transition: 'rebalance', season: 2 }),
      `/saves/${SAVE}/live?transition=rebalance&season=2`,
    );
    assert.deepEqual(parseRoute(`/saves/${SAVE}/live`, '?transition=movement&season=2'), {
      name: 'live',
      saveId: SAVE,
      leagueId: null,
      round: null,
      event: null,
      selection: null,
      transition: 'movement',
      eventSeason: 2,
      group: null,
      qualifier: null,
    });
    assert.deepEqual(parseRoute(`/saves/${SAVE}/live`, '?transition=rebalance&season=3'), {
      name: 'live',
      saveId: SAVE,
      leagueId: null,
      round: null,
      event: null,
      selection: null,
      transition: 'rebalance',
      eventSeason: 3,
      group: null,
      qualifier: null,
    });
  });

  it('ignores bogus transition values instead of crashing', () => {
    const route = parseRoute(`/saves/${SAVE}/live`, '?transition=bogus&season=2');
    assert.equal(route.name, 'live');
    assert.equal((route as { transition: unknown }).transition, null);
    assert.equal((route as { eventSeason: unknown }).eventSeason, null);
  });

  it('leaves existing Live event and league URLs unchanged', () => {
    assert.equal(livePath(SAVE, { league: 7, round: 3 }), `/saves/${SAVE}/live?league=7&round=3`);
    assert.equal(
      livePath(SAVE, { event: 'qualifier', season: 2 }),
      `/saves/${SAVE}/live?event=qualifier&season=2`,
    );
  });
});

describe('MSS-053 manual lifecycle integration', () => {
  it('persists the transition once from the Dashboard, then navigates to its reveal', () => {
    const flow = read('features/dashboard/SeasonFlow.tsx');
    assert.ok(flow.includes('runNextAndReveal'), 'transitions run through a dedicated reveal handler');
    assert.ok(flow.includes('await advanceToNextEvent(saveId)'), 'the backend persists the result');
    assert.ok(
      flow.includes('livePath(saveId, { transition'),
      'the reveal is addressed by transition key and season',
    );
    assert.ok(flow.includes('sourceSeasonNumber'), 'the persisted source season addresses the result');
    assert.ok(flow.includes('navigate('), 'the UI navigates to the reveal after persisting');
    const persistAt = flow.indexOf('await advanceToNextEvent(saveId)');
    const navigateAt = flow.indexOf('navigate(livePath(saveId, { transition');
    assert.ok(persistAt >= 0 && navigateAt > persistAt, 'persist happens once, before navigation');
  });

  it('derives the reveal from the executed action so retries stay on the right result', () => {
    const flow = read('features/dashboard/SeasonFlow.tsx');
    assert.ok(flow.includes('transitionForAction(result.executedAction)'), 'the executed action names the reveal');
    assert.ok(flow.includes('next.liveTransition'), 'the pending step carries its transition reveal');
    assert.ok(flow.includes('if (running || next?.kind !== \'event\' || !next.liveTransition)'), 'duplicate submissions are blocked');
  });

  it('labels transition steps as reveal presentations, not silent mutations', () => {
    const steps = read('features/dashboard/seasonFlowSteps.ts');
    assert.ok(steps.includes('liveTransition'), 'the flow model carries the transition reveal');
    assert.ok(steps.includes('transitionForAction(legalAction)'), 'each legal action maps to its reveal');
    assert.ok(steps.includes('Resolve & reveal promotion'), 'movement copy promises the reveal');
    assert.ok(steps.includes('Rebalance & reveal feeders'), 'rebalance copy promises the reveal');
    assert.ok(steps.includes('Form Superleague & reveal'), 'inaugural copy promises the reveal');
    const flow = read('features/dashboard/SeasonFlow.tsx');
    assert.ok(flow.includes('on Live'), 'the action reuses the Live presentation language');
  });

  it('renders the transition reveal on Live from the route, not from transient state', () => {
    const app = read('App.tsx');
    assert.ok(app.includes('TransitionRevealView'), 'Live dispatches the transition reveal view');
    assert.ok(app.includes('route.transition'), 'the URL transition key drives the presentation');
    assert.ok(
      app.includes('key={`${route.transition}:${eventSeason}`}'),
      'a new transition identity remounts the presentation',
    );
  });
});

describe('MSS-053 first-time versus historical reveal modes', () => {
  it('starts the first-time presentation face-down for both transitions', () => {
    for (const rel of ['features/movement/MovementReveal.tsx', 'features/rebalance/RebalanceReveal.tsx']) {
      const reveal = read(rel);
      assert.ok(reveal.includes("initialMode"), `${rel} distinguishes first-time from historical mode`);
      assert.ok(
        reveal.includes("initialMode === 'reveal' ? 0 : total"),
        `${rel} starts face-down in reveal mode`,
      );
      assert.ok(reveal.includes('[revealKey, initialMode]'), `${rel} restarts on identity or mode change`);
    }
  });

  it('keeps historical Standings views on the completed final state with replay', () => {
    for (const rel of ['features/movement/MovementSection.tsx', 'features/rebalance/RebalanceSection.tsx']) {
      const section = read(rel);
      assert.ok(section.includes('initialMode="complete"'), `${rel} opens history fully revealed`);
    }
    const page = read('features/standings/StandingsPage.tsx');
    assert.ok(page.includes('MovementSection'), 'standings keeps the historical movement surface');
    assert.ok(page.includes('RebalanceSection'), 'standings keeps the historical rebalance surface');
    assert.ok(
      read('features/movement/MovementReveal.tsx').includes('Replay the reveal'),
      'movement history can replay the reveal',
    );
    assert.ok(
      read('features/rebalance/RebalanceReveal.tsx').includes('Replay the reveal'),
      'rebalance history can replay the reveal',
    );
  });

  it('opens the Live transition reveal in progressive mode for every presentation', () => {
    const view = read('features/transitions/TransitionRevealView.tsx');
    const modes = [...view.matchAll(/^\s+initialMode="(\w+)"$/gm)].map((match) => match[1]);
    assert.deepEqual(
      modes,
      ['reveal', 'reveal', 'reveal'],
      'inaugural, movement and rebalance first-time presentations all start progressive',
    );
  });

  it('integrates the Season 1 inaugural formation consistently', () => {
    const view = read('features/transitions/TransitionRevealView.tsx');
    assert.ok(view.includes('season === 1'), 'Season 1 resolves through the inaugural roster');
    assert.ok(view.includes('fetchInauguralRoster'), 'the inaugural roster is read, not recomputed');
    assert.ok(view.includes('inaugural Superleague'), 'the inaugural presentation keeps its identity');
  });
});

describe('MSS-053 reveal safety and continuation', () => {
  it('never mutates sporting state from the reveal: reload re-reads persisted facts', () => {
    const view = read('features/transitions/TransitionRevealView.tsx');
    assert.ok(view.includes('fetchAutomaticMovement'), 'movement reload re-reads the persisted result');
    assert.ok(view.includes('fetchRebalanceResult'), 'rebalance reload re-reads the persisted result');
    assert.ok(!view.includes('advance-next-event'), 'the reveal never advances the lifecycle');
    assert.ok(!view.includes('advanceToNextEvent'), 'the reveal never executes a lifecycle step');
    assert.ok(!view.includes("method: 'POST'"), 'the reveal never mutates sporting state');
    assert.ok(!view.includes('Math.random'), 'the reveal never rerolls pool draws');
  });

  it('continues explicitly back to the postseason without running the next step', () => {
    const view = read('features/transitions/TransitionRevealView.tsx');
    assert.ok(view.includes('Continue on the Dashboard'), 'an explicit Continue action exists');
    assert.ok(view.includes('dashboardPath(saveId)'), 'Continue returns to the save Dashboard');
    assert.ok(!view.includes('RunQualifier'), 'finishing movement never runs the qualifier');
    assert.ok(!view.includes('SelectColorCup'), 'finishing rebalance never selects Cup squads');
    assert.ok(!view.includes('SelectTypeCup'), 'finishing rebalance never selects Cup squads');
  });

  it('explains the not-yet-resolved edge without mutating', () => {
    const view = read('features/transitions/TransitionRevealView.tsx');
    assert.ok(view.includes('not resolved yet'), 'direct visits before resolution explain themselves');
    assert.ok(view.includes('status === 404'), 'a missing persisted result is a guidance state, not an error');
  });

  it('leaves fast-forward non-interactive', () => {
    const fast = read('features/dashboard/FastForwardSeason.tsx');
    assert.ok(fast.includes('completeSeason'), 'fast-forward still bulk-completes league stages only');
    assert.ok(!fast.includes('TransitionReveal'), 'fast-forward never forces interactive reveals');
    assert.ok(!fast.includes('MovementReveal'), 'fast-forward never mounts transition reveals');
    assert.ok(!fast.includes('RebalanceReveal'), 'fast-forward never mounts transition reveals');
  });

  it('preserves the MSS-050 information in the movement reveal', () => {
    const reveal = read('features/movement/MovementReveal.tsx');
    for (const token of ['PROMOTED', 'RELEGATED', 'fromLeagueName', 'toLeagueName', 'League boundary', 'Final summary']) {
      assert.ok(reveal.includes(token), `movement reveal keeps ${token}`);
    }
  });

  it('preserves the MSS-051 information in the rebalance reveal', () => {
    const model = read('features/rebalance/rebalanceModel.ts');
    for (const token of ['TO SUPERLEAGUE', 'RETURNING', 'TO COMMON POOL', 'DRAWN FROM POOL']) {
      assert.ok(model.includes(token), `rebalance reveal keeps ${token}`);
    }
    const reveal = read('features/rebalance/RebalanceReveal.tsx');
    for (const token of ['32 / 32', 'Final summary', 'Per-league counts']) {
      assert.ok(reveal.includes(token), `rebalance reveal keeps ${token}`);
    }
  });

  it('respects reduced-motion settings exactly as the existing reveals do', () => {
    for (const rel of ['features/movement/MovementReveal.tsx', 'features/rebalance/RebalanceReveal.tsx']) {
      assert.ok(read(rel).includes('prefers-reduced-motion'), `${rel} keeps reduced-motion support`);
    }
  });
});
