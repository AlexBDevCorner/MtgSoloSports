import type { SeasonProgress, SeasonStatus } from './dashboardApi';
import { progressLabel, type EventKey } from '../events/eventModel.ts';

/**
 * Plain-language view of the season lifecycle for the Dashboard: league play,
 * then the postseason events the backend runs one per
 * `advance-next-event` call. Pure and DOM-free so it is unit-testable; the
 * backend's legal next action stays authoritative for what runs next.
 */

export type FlowStepState = 'done' | 'current' | 'upcoming';

export interface FlowStep {
  key: string;
  label: string;
  detail?: string;
  state: FlowStepState;
}

export type FlowNext =
  | { kind: 'live'; label: string; explanation: string }
  | { kind: 'event'; action: string; label: string; explanation: string; liveEvent: EventKey | null };

export interface SeasonFlowView {
  seasonNumber: number;
  steps: FlowStep[];
  next: FlowNext | null;
}

export type SummaryTarget = 'standings' | 'cups' | 'live';

interface StepDefinition {
  key: string;
  label: string;
  detail?: string;
  actions: string[];
  done: boolean;
}

interface ActionCopy {
  label: string;
  explanation: string;
  summary: string;
  target: SummaryTarget;
}

function actionCopy(action: string, nextSeason: number): ActionCopy | null {
  switch (action) {
    case 'ResolveInauguralMovement':
      return {
        label: 'Form Superleague',
        explanation: `The top 4 of every feeder league form the 32-athlete Superleague for Season ${nextSeason}.`,
        summary: 'Superleague formed.',
        target: 'standings',
      };
    case 'ResolveAutomaticMovement':
      return {
        label: 'Resolve promotion & relegation',
        explanation:
          'Superleague places 25–32 are relegated, feeder champions are promoted, and places 17–24 go to the qualifier.',
        summary: 'Promotion and relegation resolved.',
        target: 'standings',
      };
    case 'RunQualifier':
      return {
        label: 'Run qualifier',
        explanation:
          'Superleague places 17–24 face feeder runners-up (places 2–4) for the last 8 Superleague places.',
        summary: 'Qualifier finished.',
        target: 'standings',
      };
    case 'RebalanceFeeders':
      return {
        label: 'Rebalance feeder leagues',
        explanation: 'Every feeder league is refilled to 32 athletes from its color pool.',
        summary: 'Feeder leagues rebalanced.',
        target: 'standings',
      };
    case 'SelectColorCup':
      return {
        label: 'Select Color Cup field',
        explanation: 'Picks 4 athletes per sporting color — 32 in total — for the Color Cup.',
        summary: 'Cup field selected.',
        target: 'cups',
      };
    case 'SelectTypeCup':
      return {
        label: 'Select Type Cup field',
        explanation: 'Allocates athletes to creature-type teams for the Type Cup.',
        summary: 'Cup field selected.',
        target: 'cups',
      };
    case 'RunColorCupIndividual':
      return {
        label: 'Run Color Cup — individual',
        explanation: 'The 32 selected athletes race one 16-round stage for the individual title.',
        summary: 'Cup individual event finished.',
        target: 'cups',
      };
    case 'RunColorCupTeam':
      return {
        label: 'Run Color Cup — team',
        explanation: 'Color teams meet in four rank groups of 8 rounds for the team title.',
        summary: 'Cup team event finished.',
        target: 'cups',
      };
    case 'RunTypeCupTeam':
      return {
        label: 'Run Type Cup — team',
        explanation: 'Creature-type teams meet in four rank groups of 8 rounds for the team title.',
        summary: 'Cup team event finished.',
        target: 'cups',
      };
    case 'StartNextSeason':
      return {
        label: `Start Season ${nextSeason}`,
        explanation: `Opens Season ${nextSeason} for league play.`,
        summary: `Season ${nextSeason} started.`,
        target: 'live',
      };
    default:
      return null;
  }
}

function stepDefinitions(progress: SeasonProgress, status: SeasonStatus | null, season: number): StepDefinition[] {
  const leagueDone = status ? status.isCurrentSeasonComplete : progress.isSeasonComplete;
  const league: StepDefinition = {
    key: 'league',
    label: 'League play',
    detail: leagueDone ? undefined : `Stage ${progress.globalStage}/32`,
    actions: ['CompleteNextGlobalStage'],
    done: leagueDone,
  };
  if (!status) {
    return [league];
  }
  const inaugural = status.isInauguralTransition || season === 1;
  const colorCup = status.expectedCup === 'TypeCup' ? false : status.expectedCup === 'ColorCup' || season % 2 === 1;
  const cupName = colorCup ? 'Color Cup' : 'Type Cup';
  const nextSeason = status.nextSeasonNumber ?? season + 1;
  const steps: StepDefinition[] = [
    league,
    {
      key: 'movement',
      label: inaugural ? 'Form Superleague' : 'Promotion & relegation',
      actions: ['ResolveInauguralMovement', 'ResolveAutomaticMovement'],
      done: status.movementResolved,
    },
  ];
  if (!inaugural) {
    steps.push({ key: 'qualifier', label: 'Qualifier', actions: ['RunQualifier'], done: status.qualifierResolved });
  }
  steps.push(
    { key: 'rebalance', label: 'Rebalance feeders', actions: ['RebalanceFeeders'], done: status.rebalanced },
    {
      key: 'cupField',
      label: `${cupName} field`,
      actions: ['SelectColorCup', 'SelectTypeCup'],
      done: status.cupSelectionResolved,
    },
  );
  if (colorCup) {
    steps.push({
      key: 'cupIndividual',
      label: 'Cup: individual',
      actions: ['RunColorCupIndividual'],
      done: status.cupIndividualResolved,
    });
  }
  steps.push(
    { key: 'cupTeam', label: 'Cup: team', actions: ['RunColorCupTeam', 'RunTypeCupTeam'], done: status.cupTeamResolved },
    { key: 'nextSeason', label: `Start Season ${nextSeason}`, actions: ['StartNextSeason'], done: false },
  );
  return steps;
}

export function seasonFlow(progress: SeasonProgress, status: SeasonStatus | null): SeasonFlowView {
  const season = status?.sourceSeasonNumber ?? progress.seasonNumber;
  const definitions = stepDefinitions(progress, status, season);
  const legalAction = status?.legalNextActions[0] ?? null;
  const currentIndex = legalAction
    ? definitions.findIndex((step) => step.actions.includes(legalAction))
    : definitions.findIndex((step) => !step.done);

  const eventDetail = status?.eventProgress ? progressLabel(status.eventProgress) : undefined;
  const steps = definitions.map((step, index): FlowStep => {
    const state: FlowStepState =
      index === currentIndex ? 'current' : step.done || (currentIndex >= 0 && index < currentIndex) ? 'done' : 'upcoming';
    const detail = state === 'current' && eventDetail ? eventDetail : step.detail;
    return detail ? { key: step.key, label: step.label, detail, state } : { key: step.key, label: step.label, state };
  });

  return { seasonNumber: season, steps, next: nextFor(progress, status, season, legalAction) };
}

function nextFor(
  progress: SeasonProgress,
  status: SeasonStatus | null,
  season: number,
  legalAction: string | null,
): FlowNext | null {
  const leagueRunning = status ? legalAction === 'CompleteNextGlobalStage' : !progress.isSeasonComplete;
  if (leagueRunning) {
    return {
      kind: 'live',
      label: `Play stage ${progress.globalStage}`,
      explanation: 'Run rounds for every league on Live, or fast-forward the rest of the league season.',
    };
  }
  if (!status || !legalAction) {
    return null;
  }
  const liveEvent = status.eventProgress ? status.eventProgress.event : null;
  const copy = actionCopy(legalAction, status.nextSeasonNumber ?? season + 1);
  if (!copy) {
    return { kind: 'event', action: legalAction, label: 'Run next event', explanation: status.nextActionDetail, liveEvent };
  }
  return { kind: 'event', action: legalAction, label: copy.label, explanation: copy.explanation, liveEvent };
}

/** One-line confirmation shown after a step runs, plus where its results live. */
export function executedSummary(action: string, nextSeason: number): { text: string; target: SummaryTarget } {
  const copy = actionCopy(action, nextSeason);
  return copy ? { text: copy.summary, target: copy.target } : { text: 'Event finished.', target: 'standings' };
}
