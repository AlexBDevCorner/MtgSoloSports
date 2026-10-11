import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  summarizeCaps,
  summarizeCareer,
  summarizeMedals,
  summarizeSeasonTiers,
  summarizeTitleTiers,
  tierForSeason,
} from './athleteCareerSummary.ts';

describe('tierForSeason', () => {
  it('resolves tiers from data without parsing league names', () => {
    assert.equal(
      tierForSeason({ wasActive: true, leagueLevel: 'Superleague', feederDivision: null, leagueKind: 1 }),
      'superleague',
    );
    assert.equal(
      tierForSeason({ wasActive: true, leagueLevel: 'Feeder1', feederDivision: 1, leagueKind: 0 }),
      'feeder1',
    );
    assert.equal(
      tierForSeason({ wasActive: true, leagueLevel: 'Feeder2', feederDivision: 2, leagueKind: 0 }),
      'feeder2',
    );
    assert.equal(
      tierForSeason({ wasActive: true, leagueLevel: 'Feeder3', feederDivision: 3, leagueKind: 0 }),
      'feeder3',
    );
    assert.equal(
      tierForSeason({ wasActive: false, leagueLevel: null, feederDivision: null, leagueKind: null }),
      'pool',
    );
  });

  it('counts historical v1 single-feeder rows as Feeder 1', () => {
    // Backend canonical mapping (LeagueEntityLevels) plus the prestige rule:
    // v1 feeder titles count as Feeder 1 without pretending F2/F3 existed.
    assert.equal(
      tierForSeason({ wasActive: true, leagueLevel: 'Feeder1', feederDivision: 0, leagueKind: 0 }),
      'feeder1',
    );
    assert.equal(
      tierForSeason({ wasActive: true, leagueLevel: null, feederDivision: 0, leagueKind: 0 }),
      'feeder1',
    );
    assert.equal(
      tierForSeason({ wasActive: true, leagueLevel: null, feederDivision: null, leagueKind: 0 }),
      'feeder1',
    );
  });

  it('resolves Superleague from the numeric kind when the level string is absent', () => {
    assert.equal(
      tierForSeason({ wasActive: true, leagueLevel: null, feederDivision: null, leagueKind: 1 }),
      'superleague',
    );
  });
});

describe('summarizeSeasonTiers', () => {
  it('counts each season once across all five buckets', () => {
    const counts = summarizeSeasonTiers([
      { wasActive: true, leagueLevel: 'Superleague', feederDivision: null, leagueKind: 1 },
      { wasActive: true, leagueLevel: 'Superleague', feederDivision: null, leagueKind: 1 },
      { wasActive: true, leagueLevel: 'Feeder1', feederDivision: 1, leagueKind: 0 },
      { wasActive: true, leagueLevel: 'Feeder2', feederDivision: 2, leagueKind: 0 },
      { wasActive: true, leagueLevel: 'Feeder3', feederDivision: 3, leagueKind: 0 },
      { wasActive: false, leagueLevel: null, feederDivision: null, leagueKind: null },
    ]);
    assert.deepEqual(counts, { superleague: 2, feeder1: 1, feeder2: 1, feeder3: 1, pool: 1 });
  });

  it('shows zero for valid zero totals and handles new athletes', () => {
    assert.deepEqual(summarizeSeasonTiers([]), {
      superleague: 0,
      feeder1: 0,
      feeder2: 0,
      feeder3: 0,
      pool: 0,
    });
    const poolOnly = summarizeSeasonTiers([
      { wasActive: false, leagueLevel: null, feederDivision: null, leagueKind: null },
      { wasActive: false, leagueLevel: null, feederDivision: null, leagueKind: null },
    ]);
    assert.deepEqual(poolOnly, { superleague: 0, feeder1: 0, feeder2: 0, feeder3: 0, pool: 2 });
  });

  it('counts legacy feeder seasons as Feeder 1 without inventing F2/F3 seasons', () => {
    const counts = summarizeSeasonTiers([
      { wasActive: true, leagueLevel: 'Feeder1', feederDivision: 0, leagueKind: 0 },
      { wasActive: true, leagueLevel: 'Feeder1', feederDivision: 0, leagueKind: 0 },
    ]);
    assert.equal(counts.feeder1, 2);
    assert.equal(counts.feeder2, 0);
    assert.equal(counts.feeder3, 0);
  });
});

describe('summarizeTitleTiers', () => {
  it('counts championships per tier, distinct from appearances', () => {
    const titles = summarizeTitleTiers([
      { wasActive: true, leagueLevel: 'Superleague', feederDivision: null, leagueKind: 1, isChampion: true },
      { wasActive: true, leagueLevel: 'Superleague', feederDivision: null, leagueKind: 1, isChampion: false },
      { wasActive: true, leagueLevel: 'Feeder1', feederDivision: 1, leagueKind: 0, isChampion: true },
      { wasActive: true, leagueLevel: 'Feeder2', feederDivision: 2, leagueKind: 0, isChampion: false },
      { wasActive: true, leagueLevel: 'Feeder3', feederDivision: 3, leagueKind: 0, isChampion: true },
      { wasActive: false, leagueLevel: null, feederDivision: null, leagueKind: null, isChampion: false },
    ]);
    assert.deepEqual(titles, { superleague: 1, feeder1: 1, feeder2: 0, feeder3: 1 });
  });

  it('returns zeros for athletes with no titles', () => {
    assert.deepEqual(
      summarizeTitleTiers([
        { wasActive: true, leagueLevel: 'Feeder1', feederDivision: 1, leagueKind: 0, isChampion: false },
      ]),
      { superleague: 0, feeder1: 0, feeder2: 0, feeder3: 0 },
    );
    assert.deepEqual(summarizeTitleTiers([]), {
      superleague: 0,
      feeder1: 0,
      feeder2: 0,
      feeder3: 0,
    });
  });
});

describe('summarizeCaps', () => {
  it('counts Color and Type selections separately', () => {
    assert.deepEqual(
      summarizeCaps([
        { cupKind: 'ColorCup', sourceSeasonNumber: 1 },
        { cupKind: 'ColorCup', sourceSeasonNumber: 3 },
        { cupKind: 'TypeCup', sourceSeasonNumber: 2 },
      ]),
      { color: 2, type: 1 },
    );
  });

  it('never counts one Cup twice for duplicated rows', () => {
    assert.deepEqual(
      summarizeCaps([
        { cupKind: 'ColorCup', sourceSeasonNumber: 1 },
        { cupKind: 'ColorCup', sourceSeasonNumber: 1 },
        { cupKind: 'TypeCup', sourceSeasonNumber: 2 },
        { cupKind: 'TypeCup', sourceSeasonNumber: 2 },
      ]),
      { color: 1, type: 1 },
    );
  });

  it('returns zeros for unselected athletes and ignores unknown kinds', () => {
    assert.deepEqual(summarizeCaps([]), { color: 0, type: 0 });
    assert.deepEqual(summarizeCaps([{ cupKind: 'UnknownCup', sourceSeasonNumber: 1 }]), {
      color: 0,
      type: 0,
    });
  });
});

describe('summarizeMedals', () => {
  it('counts gold, silver and bronze across Cup types', () => {
    assert.deepEqual(
      summarizeMedals([{ medal: 'Gold' }, { medal: 'Silver' }, { medal: 'Bronze' }, { medal: 'Gold' }]),
      { gold: 2, silver: 1, bronze: 1 },
    );
  });

  it('ignores qualification-only and non-podium rows without double counting', () => {
    // Type Cup qualification legs and non-podium finishes persist None;
    // each history row counts at most once and league titles never appear here.
    assert.deepEqual(
      summarizeMedals([{ medal: 'None' }, { medal: 'None' }, { medal: 'Silver' }]),
      { gold: 0, silver: 1, bronze: 0 },
    );
    assert.deepEqual(summarizeMedals([]), { gold: 0, silver: 0, bronze: 0 });
  });
});

describe('summarizeCareer', () => {
  it('summarizes a decorated veteran consistently', () => {
    const summary = summarizeCareer(
      [
        { wasActive: true, leagueLevel: 'Superleague', feederDivision: null, leagueKind: 1, isChampion: true },
        { wasActive: true, leagueLevel: 'Superleague', feederDivision: null, leagueKind: 1, isChampion: false },
        { wasActive: true, leagueLevel: 'Feeder1', feederDivision: 1, leagueKind: 0, isChampion: true },
        { wasActive: false, leagueLevel: null, feederDivision: null, leagueKind: null, isChampion: false },
      ],
      [
        { cupKind: 'ColorCup', sourceSeasonNumber: 1 },
        { cupKind: 'TypeCup', sourceSeasonNumber: 2 },
      ],
      [{ medal: 'Gold' }, { medal: 'None' }, { medal: 'Bronze' }],
    );
    assert.deepEqual(summary.seasons, { superleague: 2, feeder1: 1, feeder2: 0, feeder3: 0, pool: 1 });
    assert.deepEqual(summary.titles, { superleague: 1, feeder1: 1, feeder2: 0, feeder3: 0 });
    assert.deepEqual(summary.caps, { color: 1, type: 1 });
    assert.deepEqual(summary.medals, { gold: 1, silver: 0, bronze: 1 });
  });
});
