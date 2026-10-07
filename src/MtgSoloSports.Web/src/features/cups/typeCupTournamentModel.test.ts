import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  drawHasGroups,
  hasQualificationStage,
  legsByRankGroup,
  orderedDrawMembers,
  orderedQualificationTeams,
  qualificationGroupLetter,
  qualificationGroupName,
  qualificationStageLabel,
  rankGroupLabel,
  semifinalAlias,
  splitQualificationTable,
  tournamentFormatLabel,
  tournamentStateLabel,
} from './typeCupTournamentModel.ts';

describe('type cup tournament model', () => {
  it('letters qualification groups from data (A, B, ... AA)', () => {
    assert.equal(qualificationGroupLetter(1), 'A');
    assert.equal(qualificationGroupLetter(2), 'B');
    assert.equal(qualificationGroupLetter(26), 'Z');
    assert.equal(qualificationGroupLetter(27), 'AA');
    assert.equal(qualificationGroupName(3), 'Qualification Group C');
  });

  it('uses Semifinal A/B only as a friendly alias for two groups', () => {
    assert.equal(semifinalAlias(1, 2), 'Semifinal A');
    assert.equal(semifinalAlias(2, 2), 'Semifinal B');
    assert.equal(semifinalAlias(1, 3), null);
    assert.equal(semifinalAlias(3, 3), null);
    assert.equal(qualificationStageLabel(1, 2), 'Qualification Group A · Semifinal A');
    assert.equal(qualificationStageLabel(3, 5), 'Qualification Group C');
  });

  it('keeps rank groups distinct from qualification groups in labels', () => {
    assert.equal(rankGroupLabel(1), 'Squad #1');
    assert.equal(rankGroupLabel(4), 'Squad #4');
  });

  it('splits a qualification table at the persisted cutoff without recomputing', () => {
    const teams = [
      { teamRank: 2, qualified: true },
      { teamRank: 4, qualified: false },
      { teamRank: 1, qualified: true },
      { teamRank: 3, qualified: false },
    ];
    const split = splitQualificationTable(teams, 2);
    assert.deepEqual(split.qualified.map((team) => team.teamRank), [1, 2]);
    assert.deepEqual(split.eliminated.map((team) => team.teamRank), [3, 4]);
    assert.equal(split.cutoffAfter, 2);
  });

  it('orders qualification teams by rank and rank-group legs by group rank', () => {
    const group = {
      teams: [
        { teamRank: 3, creatureType: 'C', teamScoreThousandths: 3, teamBaseThousandths: 0, medal: 'None', qualified: false },
        { teamRank: 1, creatureType: 'A', teamScoreThousandths: 1, teamBaseThousandths: 0, medal: 'None', qualified: true },
        { teamRank: 2, creatureType: 'B', teamScoreThousandths: 2, teamBaseThousandths: 0, medal: 'None', qualified: true },
      ],
    };
    assert.deepEqual(
      orderedQualificationTeams(group as never).map((team) => team.creatureType),
      ['A', 'B', 'C'],
    );
    const legs = [
      { groupNumber: 2, groupRank: 2, saveAthleteId: 1, athleteName: 'a', creatureType: 'A', selectionRank: 2, groupScoreThousandths: 0, baseScoreThousandths: 0, qualified: true },
      { groupNumber: 1, groupRank: 2, saveAthleteId: 2, athleteName: 'b', creatureType: 'B', selectionRank: 1, groupScoreThousandths: 0, baseScoreThousandths: 0, qualified: true },
      { groupNumber: 1, groupRank: 1, saveAthleteId: 3, athleteName: 'c', creatureType: 'C', selectionRank: 1, groupScoreThousandths: 0, baseScoreThousandths: 0, qualified: false },
    ];
    const byRank = legsByRankGroup(legs as never);
    assert.deepEqual(byRank.map((entry) => entry.groupNumber), [1, 2]);
    assert.deepEqual(byRank[0].legs.map((leg) => leg.saveAthleteId), [3, 2]);
  });

  it('sorts draw members for stable display', () => {
    assert.deepEqual(orderedDrawMembers(['Goblin', 'Elf', 'Dragon']), ['Dragon', 'Elf', 'Goblin']);
  });

  it('labels direct-final versus qualification formats from persisted facts', () => {
    assert.equal(tournamentFormatLabel({ isDirectFinal: true }), 'Direct Final');
    assert.equal(tournamentFormatLabel({ isDirectFinal: false }), 'Qualification + Final');
    assert.equal(hasQualificationStage({ isDirectFinal: false }), true);
    assert.equal(hasQualificationStage({ isDirectFinal: true }), false);
    assert.equal(drawHasGroups({ isDirectFinal: false, groups: [{} as never] }), true);
    assert.equal(drawHasGroups({ isDirectFinal: true, groups: [] }), false);
  });

  it('states tournament progress from persisted tables only', () => {
    const base = {
      isDirectFinal: false,
      qualificationGroupCount: 2,
      qualificationGroups: [],
      final: null,
    };
    assert.match(tournamentStateLabel(base as never), /In progress/);
    assert.match(
      tournamentStateLabel({ ...base, qualificationGroups: [{}, {}] } as never),
      /Final pending/,
    );
    assert.equal(tournamentStateLabel({ ...base, qualificationGroups: [{}, {}], final: {} } as never), 'Completed');
    assert.equal(tournamentStateLabel({ isDirectFinal: true, final: {} } as never), 'Completed');
  });
});
