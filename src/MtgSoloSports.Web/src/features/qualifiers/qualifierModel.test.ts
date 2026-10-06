import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import type { QualifierEvent, QualifierList } from './qualifierApi.ts';
import {
  buildQualifierOverview,
  qualifierFieldRows,
  qualifierOutcomeLabel,
  qualifierProgressLine,
  qualifierRoleTier,
} from './qualifierModel.ts';

function event(overrides: Partial<QualifierEvent> & { boundary: string }): QualifierEvent {
  return {
    saveId: 'save',
    fromSeasonNumber: 2,
    toSeasonNumber: 3,
    boundaryId: 1,
    sportingColor: 0,
    sportingColorName: 'White',
    qualifierSize: 16,
    roundCount: 16,
    winners: 8,
    checksum: 'abc',
    standings: [],
    ...overrides,
  };
}

function list(events: QualifierEvent[]): QualifierList {
  return { saveId: 'save', fromSeasonNumber: 2, toSeasonNumber: 3, events };
}

describe('buildQualifierOverview', () => {
  it('shows 17 canonical entries for tiered saves with completion state', () => {
    const overview = buildQualifierOverview(
      list([
        event({ boundary: 'Superleague', sportingColor: null, qualifierSize: 32, winners: 8 }),
        event({ boundary: 'Feeder1Feeder2', sportingColor: 0 }),
      ]),
      true,
    );
    assert.equal(overview.length, 17);
    assert.equal(overview[0]!.boundary, 'Superleague');
    assert.equal(overview[0]!.status, 'complete');
    assert.equal(overview[0]!.qualifierSize, 32);
    const white = overview.find((entry) => entry.key === 'Feeder1Feeder2:0')!;
    assert.equal(white.status, 'complete');
    const pending = overview.find((entry) => entry.key === 'Feeder2Feeder3:7')!;
    assert.equal(pending.status, 'pending');
    assert.equal(pending.qualifierSize, null);
  });

  it('shows only the Superleague qualifier for v1 saves', () => {
    const overview = buildQualifierOverview(
      list([event({ boundary: 'Superleague', sportingColor: null, qualifierSize: 32, winners: 8 })]),
      false,
    );
    assert.equal(overview.length, 1);
    assert.equal(overview[0]!.status, 'complete');
  });

  it('shows a pending Superleague qualifier when nothing resolved', () => {
    const overview = buildQualifierOverview(null, true);
    assert.equal(overview.length, 17);
    assert.ok(overview.every((entry) => entry.status === 'pending'));
  });
});

describe('qualifierFieldRows', () => {
  it('orders by qualifier rank for 16 and 32-athlete fields', () => {
    const rows = qualifierFieldRows(
      event({
        boundary: 'Feeder2Feeder3',
        sportingColor: 3,
        standings: [
          {
            athleteId: 2,
            name: 'Card Two',
            sportingColor: 'Black',
            role: 'Challenger',
            fromLeagueId: 5,
            fromLeagueName: 'Black League F3',
            fromSeasonRank: 9,
            qualifierRank: 2,
            qualifierScoreThousandths: 1000,
            baseScoreThousandths: 900,
            roundWins: 1,
            isQualified: true,
          },
          {
            athleteId: 1,
            name: 'Card One',
            sportingColor: 'Black',
            role: 'Incumbent',
            fromLeagueId: 4,
            fromLeagueName: 'Black League F2',
            fromSeasonRank: 17,
            qualifierRank: 1,
            qualifierScoreThousandths: 2000,
            baseScoreThousandths: 1800,
            roundWins: 2,
            isQualified: true,
          },
        ],
      }),
    );
    assert.equal(rows.length, 2);
    assert.equal(rows[0]!.athleteId, 1);
    assert.equal(qualifierOutcomeLabel(rows[0]!), 'QUALIFIED');
    assert.equal(qualifierOutcomeLabel({ ...rows[1]!, isQualified: false }), 'ELIMINATED');
  });

  it('describes progress from persisted facts', () => {
    const [entry] = buildQualifierOverview(
      list([event({ boundary: 'Superleague', sportingColor: null, qualifierSize: 32, winners: 8 })]),
      false,
    );
    assert.ok(qualifierProgressLine(entry!).includes('32 athletes'));
    assert.ok(qualifierProgressLine(entry!).includes('top 8 qualify'));
  });

  it('names the source tier per role from the boundary bands', () => {
    assert.equal(qualifierRoleTier('Incumbent', 'Superleague'), 'Superleague');
    assert.equal(qualifierRoleTier('Challenger', 'Superleague'), 'Feeder 1');
    assert.equal(qualifierRoleTier('Incumbent', 'Feeder1Feeder2'), 'Feeder 1');
    assert.equal(qualifierRoleTier('Challenger', 'Feeder1Feeder2'), 'Feeder 2');
    assert.equal(qualifierRoleTier('Incumbent', 'Feeder2Feeder3'), 'Feeder 2');
    assert.equal(qualifierRoleTier('Challenger', 'Feeder2Feeder3'), 'Feeder 3');
  });
});
