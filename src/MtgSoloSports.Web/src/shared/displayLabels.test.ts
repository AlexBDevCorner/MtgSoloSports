import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  KNOWN_CAREER_RECORD_KEYS,
  KNOWN_CUP_KINDS,
  KNOWN_HONOUR_KINDS,
  KNOWN_LIFECYCLE_ACTIONS,
  KNOWN_PHASES,
  KNOWN_STORY_EVENT_TYPES,
  careerRecordLabel,
  cupHistoryEventLabel,
  cupHistoryEventName,
  cupLabel,
  honourKindLabel,
  humanizeDiagnostic,
  isKnownCareerRecordKey,
  isKnownCupKind,
  isKnownHonourKind,
  isKnownLifecycleAction,
  isKnownPhase,
  isKnownStoryEventType,
  lifecycleActionLabel,
  normalizeCupSpelling,
  phaseLabel,
  scoringRecordLabel,
  scoringScopeLabel,
  storyEventLabel,
} from './displayLabels.ts';

describe('MSS-072 display labels', () => {
  it('maps every persisted season phase to readable Title Case', () => {
    assert.equal(phaseLabel('SeasonInProgress'), 'Season In Progress');
    assert.equal(phaseLabel('SeasonComplete'), 'Season Complete');
    assert.equal(phaseLabel('InauguralMovementResolved'), 'Superleague Formed');
    assert.equal(phaseLabel('AutomaticMovementResolved'), 'Promotion and Relegation Resolved');
    assert.equal(phaseLabel('QualifierResolved'), 'Qualifier Resolved');
    assert.equal(phaseLabel('Rebalanced'), 'Feeders Rebalanced');
    assert.equal(phaseLabel('CupSelectionResolved'), 'Cup Squads Selected');
    assert.equal(phaseLabel('CupIndividualResolved'), 'Cup Individual Complete');
    // Reported case: Dashboard CupComplete reads as Cup Complete.
    assert.equal(phaseLabel('CupComplete'), 'Cup Complete');
    assert.equal(KNOWN_PHASES.length, 9);
    for (const phase of KNOWN_PHASES) {
      assert.ok(isKnownPhase(phase), `known phase: ${phase}`);
      assert.ok(!phaseLabel(phase).includes('CupComplete'), 'no raw identifier leaks');
    }
  });

  it('maps every honour kind to natural sentence case with proper nouns intact', () => {
    assert.equal(honourKindLabel('FeederRunnerUp'), 'Feeder runner-up');
    assert.equal(honourKindLabel('ColorCupIndividualRunnerUp'), 'Color Cup individual runner-up');
    assert.equal(honourKindLabel('SuperleagueTitle'), 'Superleague title');
    assert.equal(honourKindLabel('FeederTitle'), 'Feeder title');
    assert.equal(honourKindLabel('SuperleagueRunnerUp'), 'Superleague runner-up');
    assert.equal(honourKindLabel('SuperleagueThirdPlace'), 'Superleague third place');
    assert.equal(honourKindLabel('FeederThirdPlace'), 'Feeder third place');
    assert.equal(honourKindLabel('ColorCupIndividualChampion'), 'Color Cup individual champion');
    assert.equal(honourKindLabel('ColorCupIndividualThirdPlace'), 'Color Cup individual third place');
    assert.equal(honourKindLabel('ColorCupTeamChampion'), 'Color Cup team champion');
    assert.equal(honourKindLabel('ColorCupTeamRunnerUp'), 'Color Cup team runner-up');
    assert.equal(honourKindLabel('ColorCupTeamThirdPlace'), 'Color Cup team third place');
    assert.equal(honourKindLabel('TypeCupTeamChampion'), 'Type Cup team champion');
    assert.equal(honourKindLabel('TypeCupTeamRunnerUp'), 'Type Cup team runner-up');
    assert.equal(honourKindLabel('TypeCupTeamThirdPlace'), 'Type Cup team third place');
    assert.equal(KNOWN_HONOUR_KINDS.length, 15);
    for (const kind of KNOWN_HONOUR_KINDS) {
      assert.ok(isKnownHonourKind(kind), `known honour: ${kind}`);
    }
  });

  it('maps every Cup kind to its established competition name', () => {
    assert.equal(cupLabel('ColorCup'), 'Color Cup');
    assert.equal(cupLabel('TypeCup'), 'Type Cup');
    assert.equal(cupLabel('Color'), 'Color Cup');
    assert.equal(cupLabel('Type'), 'Type Cup');
    assert.equal(KNOWN_CUP_KINDS.length, 6);
    for (const kind of KNOWN_CUP_KINDS) {
      assert.ok(isKnownCupKind(kind), `known cup: ${kind}`);
    }
  });

  it('uses consistent sentence-case career record names', () => {
    assert.equal(careerRecordLabel('feeder_titles'), 'Feeder titles');
    assert.equal(careerRecordLabel('superleague_titles'), 'Superleague titles');
    assert.equal(careerRecordLabel('total_titles'), 'Total titles');
    assert.equal(careerRecordLabel('stage_wins'), 'Stage wins');
    assert.equal(careerRecordLabel('round_wins'), 'Round wins');
    assert.equal(careerRecordLabel('superleague_appearances'), 'Superleague appearances');
    assert.equal(careerRecordLabel('total_appearances'), 'Total appearances');
    assert.equal(careerRecordLabel('longest_superleague_tenure'), 'Longest Superleague tenure');
    assert.equal(careerRecordLabel('promotions'), 'Promotions');
    assert.equal(careerRecordLabel('relegations'), 'Relegations');
    assert.equal(careerRecordLabel('highest_effective_bonus'), 'Highest effective bonus');
    assert.equal(careerRecordLabel('longest_title_streak'), 'Longest title streak');
    assert.equal(careerRecordLabel('longest_stage_win_streak'), 'Longest stage-win streak');
    assert.equal(KNOWN_CAREER_RECORD_KEYS.length, 13);
    for (const key of KNOWN_CAREER_RECORD_KEYS) {
      assert.ok(isKnownCareerRecordKey(key), `known record: ${key}`);
    }
    // Mixed backend capitalization never reaches the UI: the mapping wins
    // over a stale label from an existing save.
    assert.equal(careerRecordLabel('feeder_titles', 'feeder titles'), 'Feeder titles');
    assert.equal(careerRecordLabel('superleague_titles', 'Superleague titles'), 'Superleague titles');
  });

  it('normalizes British Colour spelling to Color in scoring copy', () => {
    assert.equal(normalizeCupSpelling('Colour Cup individual'), 'Color Cup individual');
    assert.equal(
      scoringRecordLabel('colour_cup_individual_round_best', 'Most athlete points in single Colour Cup individual round'),
      'Most athlete points in single Color Cup individual round',
    );
    assert.equal(scoringScopeLabel('Colour Cup individual'), 'Color Cup individual');
    assert.equal(scoringScopeLabel('Colour Cup team'), 'Color Cup team');
    assert.equal(scoringScopeLabel('White League'), 'White League');
    assert.equal(scoringScopeLabel('Superleague'), 'Superleague');
  });

  it('maps every story event type to a readable badge', () => {
    assert.equal(storyEventLabel('first_stage_win'), 'First stage win');
    assert.equal(storyEventLabel('promotion'), 'Promotion');
    assert.equal(storyEventLabel('color_cup_individual_title'), 'Color Cup individual title');
    assert.equal(storyEventLabel('type_cup_team_medal'), 'Type Cup team medal');
    assert.equal(KNOWN_STORY_EVENT_TYPES.length, 14);
    for (const eventType of KNOWN_STORY_EVENT_TYPES) {
      assert.ok(isKnownStoryEventType(eventType), `known story: ${eventType}`);
      assert.ok(!storyEventLabel(eventType).includes('_'), 'no snake_case leaks');
    }
  });

  it('maps every lifecycle action to a short human label', () => {
    assert.equal(lifecycleActionLabel('CompleteNextGlobalStage'), 'Complete global stage');
    assert.equal(lifecycleActionLabel('SelectColorCup'), 'Select Color Cup squads');
    assert.equal(lifecycleActionLabel('SelectTypeCup'), 'Select Type Cup squads');
    assert.equal(lifecycleActionLabel('StartNextSeason'), 'Start next season');
    assert.equal(KNOWN_LIFECYCLE_ACTIONS.length, 11);
    for (const action of KNOWN_LIFECYCLE_ACTIONS) {
      assert.ok(isKnownLifecycleAction(action), `known action: ${action}`);
    }
  });

  it('labels Cup history rows without duplicating or leaking identifiers', () => {
    assert.equal(cupHistoryEventLabel('Individual'), 'Individual');
    assert.equal(cupHistoryEventLabel('Team'), 'Team');
    assert.equal(cupHistoryEventName('Color', 'Individual'), 'Color Cup — individual');
    assert.equal(cupHistoryEventName('Color', 'Team'), 'Color Cup — team');
    assert.equal(cupHistoryEventName('Type', 'Team'), 'Type Cup — team');
  });

  it('falls back to a readable split for unknown diagnostics instead of raw identifiers', () => {
    assert.equal(humanizeDiagnostic('CupComplete'), 'Cup Complete');
    assert.equal(humanizeDiagnostic('first_stage_win'), 'First stage win');
    assert.equal(humanizeDiagnostic(''), '—');
    assert.equal(humanizeDiagnostic(null), '—');
    // Unknown future values stay detectable and readable.
    assert.ok(!isKnownPhase('CupCompletePlus'));
    assert.equal(phaseLabel('CupCompletePlus'), 'Cup Complete Plus');
    assert.ok(!isKnownHonourKind('FeederChampion'));
    assert.equal(honourKindLabel('FeederChampion'), 'Feeder Champion');
    assert.ok(!isKnownStoryEventType('color_cup_grand_title'));
    assert.equal(storyEventLabel('color_cup_grand_title'), 'Color cup grand title');
    assert.ok(!isKnownCareerRecordKey('mystery_record'));
    assert.equal(careerRecordLabel('mystery_record', 'mystery record'), 'mystery record');
    assert.equal(careerRecordLabel('mystery_record'), 'Mystery record');
    assert.equal(cupLabel('MysteryCup'), 'Mystery Cup');
  });
});
