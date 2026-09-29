import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  allStageColumns,
  buildCellMap,
  cellTitle,
  completedStageNumbers,
  formatBonus,
  formatPoints,
  maxCompletedStage,
  placeLabel,
  podiumClass,
  sortMatrixRows,
  visibleStageColumns,
  type CombinedMatrixRow,
} from './matrix.ts';
import type { SeasonPlacementCell } from './standingsApi.ts';

function cell(athleteId: number, stageNumber: number, stageRank: number): SeasonPlacementCell {
  return {
    athleteId,
    stageNumber,
    stageRank,
    earnedBonusThousandths: stageRank === 1 ? 200 : 0,
    championshipPointsThousandths: 77000,
    stageScoreThousandths: 100000,
    roundWins: stageRank === 1 ? 3 : 0,
  };
}

function row(
  athleteId: number,
  name: string,
  seasonRank: number | null,
  stageWins: number | null,
  points: number | null,
): CombinedMatrixRow {
  return {
    athleteId,
    name,
    seasonRank,
    totalChampionshipPointsThousandths: points,
    stageWins,
    roundWins: 0,
    sportingColorName: 'White',
    imageUrl: null,
    currentEffectiveBonusThousandths: 0,
  };
}

describe('standings matrix alignment', () => {
  it('maps each athlete to its actual P1-P32 per completed stage', () => {
    const placements = [cell(1, 1, 1), cell(2, 1, 2), cell(1, 2, 5), cell(2, 2, 1)];
    const map = buildCellMap(placements);
    assert.equal(map.get(1)?.get(1)?.stageRank, 1);
    assert.equal(map.get(2)?.get(1)?.stageRank, 2);
    assert.equal(map.get(1)?.get(2)?.stageRank, 5);
    assert.equal(map.get(2)?.get(2)?.stageRank, 1);
  });

  it('keeps completed stages chronological 1-32', () => {
    assert.deepEqual(allStageColumns().slice(0, 3), [1, 2, 3]);
    assert.equal(allStageColumns().length, 32);
    assert.equal(allStageColumns()[31], 32);
  });

  it('leaves incomplete and future stages blank, never zero-filled', () => {
    const placements = [cell(1, 1, 1), cell(2, 1, 32)];
    const map = buildCellMap(placements);
    assert.equal(map.get(1)?.get(2), undefined);
    assert.equal(map.get(1)?.get(32), undefined);
    assert.deepEqual(completedStageNumbers(placements), [1]);
    assert.equal(maxCompletedStage(placements), 1);
    assert.equal(maxCompletedStage([]), null);
  });

  it('exposes earned bonus distinctly from points in cell details', () => {
    const winner = cell(7, 3, 1);
    const title = cellTitle('Grizzly Bears', 3, winner);
    assert.match(title, /P1/);
    assert.match(title, /earned bonus/);
    assert.match(title, /next stage/);
    const missing = cellTitle('Grizzly Bears', 4, undefined);
    assert.match(missing, /not yet completed/);
  });
});

describe('standings matrix historical switching', () => {
  it('aligns rows to the selected season roster, not another season', () => {
    const seasonOne = [cell(1, 1, 1), cell(2, 1, 2)];
    const seasonTwo = [cell(3, 1, 1), cell(4, 1, 2)];
    const mapOne = buildCellMap(seasonOne);
    const mapTwo = buildCellMap(seasonTwo);
    assert.equal(mapOne.get(3), undefined);
    assert.equal(mapTwo.get(1), undefined);
    assert.equal(mapTwo.get(3)?.get(1)?.stageRank, 1);
  });
});

describe('standings matrix sorting', () => {
  it('sorts by season rank by default with a stable name tiebreak', () => {
    const rows = [row(1, 'Zebra', 2, 1, 100000), row(2, 'Apple', 1, 0, 50000)];
    assert.deepEqual(
      sortMatrixRows(rows, 'rank').map((entry) => entry.athleteId),
      [2, 1],
    );
  });

  it('compares by stage wins without moving stage columns', () => {
    const rows = [
      row(1, 'A', 1, 0, 200000),
      row(2, 'B', 2, 3, 50000),
      row(3, 'C', 3, 1, 100000),
    ];
    assert.deepEqual(
      sortMatrixRows(rows, 'wins').map((entry) => entry.athleteId),
      [2, 3, 1],
    );
  });

  it('compares by championship points with rank as the stable fallback', () => {
    const rows = [
      row(1, 'A', 3, 0, 300000),
      row(2, 'B', 1, 5, 100000),
      row(3, 'C', 2, 1, 200000),
    ];
    assert.deepEqual(
      sortMatrixRows(rows, 'points').map((entry) => entry.athleteId),
      [1, 3, 2],
    );
  });

  it('places unranked preseason rows after ranked rows', () => {
    const rows = [row(1, 'B', null, 0, null), row(2, 'A', 1, 0, 77000)];
    assert.deepEqual(
      sortMatrixRows(rows, 'rank').map((entry) => entry.athleteId),
      [2, 1],
    );
  });
});

describe('standings matrix presentation', () => {
  it('labels places and distinguishes wins and podiums without color alone', () => {
    assert.equal(placeLabel(1), 'P1');
    assert.equal(placeLabel(32), 'P32');
    assert.equal(podiumClass(1), 'place-win');
    assert.equal(podiumClass(2), 'place-podium');
    assert.equal(podiumClass(3), 'place-podium');
    assert.equal(podiumClass(10), 'place-top10');
    assert.equal(podiumClass(11), 'place-rest');
    assert.equal(podiumClass(null), 'place-empty');
  });

  it('formats fixed-point points and bonus for display only', () => {
    assert.equal(formatPoints(77000), '77.000');
    assert.equal(formatBonus(200), '+0.200');
    assert.equal(formatBonus(0), '+0.000');
  });

  it('offers a compact stage window without hiding the feature', () => {
    const placements = [cell(1, 1, 1), cell(1, 32, 2)];
    assert.equal(visibleStageColumns('all', placements).length, 32);
    assert.deepEqual(visibleStageColumns('1-8', placements), [1, 2, 3, 4, 5, 6, 7, 8]);
    assert.deepEqual(visibleStageColumns('25-32', placements), [25, 26, 27, 28, 29, 30, 31, 32]);
    assert.deepEqual(visibleStageColumns('completed', placements), [1, 32]);
    assert.equal(visibleStageColumns('completed', []).length, 0);
  });
});
