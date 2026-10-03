import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { formatScoringContext, groupScoringRecords } from './scoringFormat.ts';
import type { ScoringRecord } from './recordsApi.ts';

describe('scoringFormat', () => {
  it('formats league holder context with season, league, stage and round', () => {
    const text = formatScoringContext({
      athleteId: 1,
      athleteName: 'Alpha',
      teamKey: '',
      teamName: '',
      value: 82815,
      valueDisplay: '82.815',
      seasonNumber: 1,
      competition: 'White League',
      leagueName: 'White League',
      stageNumber: 2,
      roundNumber: 4,
      groupNumber: null,
    });
    assert.equal(text, 'Season 1 · White League · Stage 2 · Round 4');
  });

  it('formats team leg holder with group and team context', () => {
    const text = formatScoringContext({
      athleteId: 7,
      athleteName: 'Beta',
      teamKey: 'White',
      teamName: 'White',
      value: 32000,
      valueDisplay: '32.000',
      seasonNumber: 1,
      competition: 'Colour Cup team',
      leagueName: null,
      stageNumber: null,
      roundNumber: 3,
      groupNumber: 1,
    });
    assert.equal(text, 'Season 1 · Colour Cup team · Group 1 · Round 3');
  });

  it('formats pure team holder without athlete', () => {
    const text = formatScoringContext({
      athleteId: null,
      athleteName: null,
      teamKey: 'Elf',
      teamName: 'Elf',
      value: 1000000,
      valueDisplay: '1000.000',
      seasonNumber: 2,
      competition: 'Type Cup team',
      leagueName: null,
      stageNumber: null,
      roundNumber: null,
      groupNumber: null,
    });
    assert.equal(text, 'Season 2 · Type Cup team');
  });

  it('groups scoring records by category without mixing formats', () => {
    const records: ScoringRecord[] = [
      {
        recordKey: 'league_single_round__white',
        label: 'Most athlete points in single White League round',
        category: 'League',
        scope: 'White League',
        value: 82815,
        valueDisplay: '82.815',
        isVacant: false,
        holders: [],
      },
      {
        recordKey: 'colour_cup_individual_round_best',
        label: 'Most athlete points in single Colour Cup individual round',
        category: 'Individual Cups',
        scope: 'Colour Cup individual',
        value: 85000,
        valueDisplay: '85.000',
        isVacant: false,
        holders: [],
      },
      {
        recordKey: 'colour_cup_team_total_best',
        label: 'Most team points in Colour Cup team event',
        category: 'Team Cups',
        scope: 'Colour Cup team',
        value: 1000000,
        valueDisplay: '1000.000',
        isVacant: false,
        holders: [],
      },
    ];
    const grouped = groupScoringRecords(records);
    assert.equal(grouped.league.length, 1);
    assert.equal(grouped.individual.length, 1);
    assert.equal(grouped.team.length, 1);
    assert.equal(grouped.league[0].recordKey, 'league_single_round__white');
  });
});
