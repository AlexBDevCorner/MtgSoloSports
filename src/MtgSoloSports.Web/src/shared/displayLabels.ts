/**
 * Centralized domain-to-display-name presentation for the React frontend (MSS-072).
 * Pure and DOM-free: every helper maps a persisted/API identifier to
 * user-facing English without touching API parameters, comparisons, routing,
 * sorting or serialization. Callers must keep raw values for logic (filtering,
 * `includes`, route keys) and use these helpers only at the presentation
 * boundary.
 *
 * Copy style (deliberate per context):
 * - Season/phase/state badges and headings: Title Case ("Cup Complete").
 * - Competitions keep established proper names ("Color Cup", "Type Cup",
 *   "Superleague").
 * - Honour, record and story descriptions read naturally in sentence case
 *   ("Feeder runner-up", "Superleague titles"), preserving proper nouns.
 * - American spelling throughout ("Color", never "Colour").
 * - Card/athlete proper names and free-form content are never rewritten.
 */

/** Fallback splitter for genuinely unknown diagnostic values only. */
export function humanizeDiagnostic(value: string | null | undefined): string {
  if (value === null || value === undefined) {
    return '—';
  }
  const trimmed = String(value).trim();
  if (trimmed.length === 0) {
    return '—';
  }
  const spaced = trimmed
    .replace(/[_-]+/g, ' ')
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/\s+/g, ' ')
    .trim();
  if (spaced.length === 0) {
    return '—';
  }
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}

/** American spelling for Cup copy ("Colour" -> "Color"). Presentation-only. */
export function normalizeCupSpelling(text: string): string {
  return text.replace(/Colour/g, 'Color').replace(/colour/g, 'color');
}

// ---------------------------------------------------------------------------
// Season / save phases (backend `SavePhase.ToString()` values, persisted text)
// ---------------------------------------------------------------------------

const PHASE_LABELS: Record<string, string> = {
  SeasonInProgress: 'Season In Progress',
  SeasonComplete: 'Season Complete',
  InauguralMovementResolved: 'Superleague Formed',
  AutomaticMovementResolved: 'Promotion and Relegation Resolved',
  QualifierResolved: 'Qualifier Resolved',
  Rebalanced: 'Feeders Rebalanced',
  CupSelectionResolved: 'Cup Squads Selected',
  CupIndividualResolved: 'Cup Individual Complete',
  CupComplete: 'Cup Complete',
};

export const KNOWN_PHASES: readonly string[] = Object.freeze(Object.keys(PHASE_LABELS));

export function isKnownPhase(value: string | null | undefined): boolean {
  return typeof value === 'string' && Object.prototype.hasOwnProperty.call(PHASE_LABELS, value);
}

export function phaseLabel(phase: string | null | undefined): string {
  if (typeof phase !== 'string' || phase.length === 0) {
    return '—';
  }
  const known = PHASE_LABELS[phase];
  if (known !== undefined) {
    return known;
  }
  return humanizeDiagnostic(phase);
}

// ---------------------------------------------------------------------------
// Cup kinds (`ColorCup`/`TypeCup` selections, `Color`/`Type` editions, routes)
// ---------------------------------------------------------------------------

const CUP_LABELS: Record<string, string> = {
  ColorCup: 'Color Cup',
  TypeCup: 'Type Cup',
  Color: 'Color Cup',
  Type: 'Type Cup',
  color: 'Color Cup',
  type: 'Type Cup',
  'Color Cup': 'Color Cup',
  'Type Cup': 'Type Cup',
};

export const KNOWN_CUP_KINDS: readonly string[] = Object.freeze([
  'ColorCup',
  'TypeCup',
  'Color',
  'Type',
  'color',
  'type',
]);

export function isKnownCupKind(value: string | null | undefined): boolean {
  return typeof value === 'string' && Object.prototype.hasOwnProperty.call(CUP_LABELS, value);
}

/** Presentation-only Cup name; never send the result back as an identifier. */
export function cupLabel(kind: string | null | undefined): string {
  if (typeof kind !== 'string' || kind.trim().length === 0) {
    return '—';
  }
  const known = CUP_LABELS[kind];
  if (known !== undefined) {
    return known;
  }
  const trimmed = kind.trim();
  const normalized = normalizeCupSpelling(trimmed);
  if (normalized !== trimmed) {
    return normalized;
  }
  return humanizeDiagnostic(kind);
}

// ---------------------------------------------------------------------------
// Honour kinds (backend `HonourKind.ToString()`, persisted integers as text)
// ---------------------------------------------------------------------------

const HONOUR_LABELS: Record<string, string> = {
  FeederTitle: 'Feeder title',
  SuperleagueTitle: 'Superleague title',
  ColorCupIndividualChampion: 'Color Cup individual champion',
  ColorCupTeamChampion: 'Color Cup team champion',
  TypeCupTeamChampion: 'Type Cup team champion',
  FeederRunnerUp: 'Feeder runner-up',
  FeederThirdPlace: 'Feeder third place',
  SuperleagueRunnerUp: 'Superleague runner-up',
  SuperleagueThirdPlace: 'Superleague third place',
  ColorCupIndividualRunnerUp: 'Color Cup individual runner-up',
  ColorCupIndividualThirdPlace: 'Color Cup individual third place',
  ColorCupTeamRunnerUp: 'Color Cup team runner-up',
  ColorCupTeamThirdPlace: 'Color Cup team third place',
  TypeCupTeamRunnerUp: 'Type Cup team runner-up',
  TypeCupTeamThirdPlace: 'Type Cup team third place',
};

export const KNOWN_HONOUR_KINDS: readonly string[] = Object.freeze(Object.keys(HONOUR_LABELS));

export function isKnownHonourKind(value: string | null | undefined): boolean {
  return typeof value === 'string' && Object.prototype.hasOwnProperty.call(HONOUR_LABELS, value);
}

export function honourKindLabel(kind: string | null | undefined): string {
  if (typeof kind !== 'string' || kind.trim().length === 0) {
    return '—';
  }
  const known = HONOUR_LABELS[kind];
  if (known !== undefined) {
    return known;
  }
  return humanizeDiagnostic(kind);
}

// ---------------------------------------------------------------------------
// Career records (stable `RecordKey` strings; backend `label` is the fallback)
// ---------------------------------------------------------------------------

const CAREER_RECORD_LABELS: Record<string, string> = {
  feeder_titles: 'Feeder titles',
  superleague_titles: 'Superleague titles',
  total_titles: 'Total titles',
  stage_wins: 'Stage wins',
  round_wins: 'Round wins',
  superleague_appearances: 'Superleague appearances',
  total_appearances: 'Total appearances',
  longest_superleague_tenure: 'Longest Superleague tenure',
  promotions: 'Promotions',
  relegations: 'Relegations',
  highest_effective_bonus: 'Highest effective bonus',
  longest_title_streak: 'Longest title streak',
  longest_stage_win_streak: 'Longest stage-win streak',
};

export const KNOWN_CAREER_RECORD_KEYS: readonly string[] = Object.freeze(Object.keys(CAREER_RECORD_LABELS));

export function isKnownCareerRecordKey(value: string | null | undefined): boolean {
  return typeof value === 'string' && Object.prototype.hasOwnProperty.call(CAREER_RECORD_LABELS, value);
}

/**
 * Consistent sentence-case record names keyed by the stable `recordKey`.
 * The backend `label` is only a fallback for unknown future keys so new
 * record types cannot silently leak `snake_case` identifiers.
 */
export function careerRecordLabel(
  recordKey: string | null | undefined,
  fallbackLabel?: string | null,
): string {
  if (typeof recordKey === 'string') {
    const known = CAREER_RECORD_LABELS[recordKey];
    if (known !== undefined) {
      return known;
    }
  }
  if (typeof fallbackLabel === 'string' && fallbackLabel.trim().length > 0) {
    return normalizeCupSpelling(fallbackLabel.trim());
  }
  return humanizeDiagnostic(recordKey);
}

// ---------------------------------------------------------------------------
// Scoring records (backend labels/scopes with British "Colour" spelling)
// ---------------------------------------------------------------------------

/** Presentation-only scoring label: keeps backend wording, fixes spelling. */
export function scoringRecordLabel(
  recordKey: string | null | undefined,
  fallbackLabel?: string | null,
): string {
  if (typeof fallbackLabel === 'string' && fallbackLabel.trim().length > 0) {
    return normalizeCupSpelling(fallbackLabel.trim());
  }
  return humanizeDiagnostic(recordKey);
}

/** Presentation-only scoring scope: keeps backend scope, fixes spelling. */
export function scoringScopeLabel(scope: string | null | undefined): string {
  if (typeof scope !== 'string' || scope.trim().length === 0) {
    return '—';
  }
  return normalizeCupSpelling(scope.trim());
}

// ---------------------------------------------------------------------------
// Story event types (`snake_case`, persisted `EventType` strings)
// ---------------------------------------------------------------------------

const STORY_EVENT_LABELS: Record<string, string> = {
  first_stage_win: 'First stage win',
  first_league_title: 'First league title',
  league_title_milestone: 'League title milestone',
  first_superleague_appearance: 'First Superleague appearance',
  promotion: 'Promotion',
  relegation: 'Relegation',
  return_from_pool: 'Return from pool',
  new_record: 'New record',
  color_cup_individual_title: 'Color Cup individual title',
  color_cup_medal: 'Color Cup medal',
  color_cup_team_title: 'Color Cup team title',
  color_cup_team_medal: 'Color Cup team medal',
  type_cup_team_title: 'Type Cup team title',
  type_cup_team_medal: 'Type Cup team medal',
};

export const KNOWN_STORY_EVENT_TYPES: readonly string[] = Object.freeze(Object.keys(STORY_EVENT_LABELS));

export function isKnownStoryEventType(value: string | null | undefined): boolean {
  return typeof value === 'string' && Object.prototype.hasOwnProperty.call(STORY_EVENT_LABELS, value);
}

export function storyEventLabel(eventType: string | null | undefined): string {
  if (typeof eventType !== 'string' || eventType.trim().length === 0) {
    return '—';
  }
  const known = STORY_EVENT_LABELS[eventType];
  if (known !== undefined) {
    return known;
  }
  return humanizeDiagnostic(eventType);
}

// ---------------------------------------------------------------------------
// Lifecycle actions (`legalNextActions` / `executedAction` identifiers)
// ---------------------------------------------------------------------------

const ACTION_LABELS: Record<string, string> = {
  CompleteNextGlobalStage: 'Complete global stage',
  ResolveInauguralMovement: 'Form Superleague',
  ResolveAutomaticMovement: 'Resolve promotion',
  RunQualifier: 'Run qualifier',
  RebalanceFeeders: 'Rebalance feeders',
  SelectColorCup: 'Select Color Cup squads',
  SelectTypeCup: 'Select Type Cup squads',
  RunColorCupIndividual: 'Run Color Cup individual',
  RunColorCupTeam: 'Run Color Cup team',
  RunTypeCupTeam: 'Run Type Cup team',
  StartNextSeason: 'Start next season',
};

export const KNOWN_LIFECYCLE_ACTIONS: readonly string[] = Object.freeze(Object.keys(ACTION_LABELS));

export function isKnownLifecycleAction(value: string | null | undefined): boolean {
  return typeof value === 'string' && Object.prototype.hasOwnProperty.call(ACTION_LABELS, value);
}

/** Short human label for a lifecycle action; never sent back to the backend. */
export function lifecycleActionLabel(action: string | null | undefined): string {
  if (typeof action !== 'string' || action.trim().length === 0) {
    return '—';
  }
  const known = ACTION_LABELS[action];
  if (known !== undefined) {
    return known;
  }
  return humanizeDiagnostic(action);
}

// ---------------------------------------------------------------------------
// Athlete Cup history (`cup`: "Color"/"Type", `event`: "Individual"/"Team")
// ---------------------------------------------------------------------------

const CUP_EVENT_LABELS: Record<string, string> = {
  Individual: 'Individual',
  Team: 'Team',
};

export function cupHistoryEventLabel(event: string | null | undefined): string {
  if (typeof event !== 'string' || event.trim().length === 0) {
    return '—';
  }
  const known = CUP_EVENT_LABELS[event];
  if (known !== undefined) {
    return known;
  }
  return humanizeDiagnostic(event);
}

/** "Color Cup — individual" style label for one Cup history row. */
export function cupHistoryEventName(
  cup: string | null | undefined,
  event: string | null | undefined,
  fallbackEventName?: string | null,
): string {
  const cupName = cupLabel(cup);
  const eventName = cupHistoryEventLabel(event).toLowerCase();
  if (cupName !== '—' && eventName !== '—') {
    return `${cupName} — ${eventName}`;
  }
  if (typeof fallbackEventName === 'string' && fallbackEventName.trim().length > 0) {
    return normalizeCupSpelling(fallbackEventName.trim());
  }
  return humanizeDiagnostic(fallbackEventName ?? `${String(cup)} ${String(event)}`);
}
