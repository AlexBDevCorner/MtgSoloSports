import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  buildLeagueGrid,
  CAREER_HOLDER_PREVIEW_COUNT,
  cupEventDisplayName,
  groupCupRecords,
  leagueMetricDisplayName,
  leagueScopeDisplayName,
  LEAGUE_METRIC_ORDER,
  LEAGUE_SCOPE_ORDER,
  resolveScoringMetric,
  resolveScoringScopeKey,
  splitHolderPreview,
} from './scoringFormat.ts';
import type { ScoringRecord } from './recordsApi.ts';

const here = dirname(fileURLToPath(import.meta.url));
const srcRoot = join(here, '..', '..');
const read = (rel: string): string => readFileSync(join(srcRoot, rel), 'utf8');

function leagueRecord(
  scopeKey: string,
  metric: string,
  overrides?: Partial<ScoringRecord>,
): ScoringRecord {
  const recordKey =
    metric === 'round'
      ? `league_single_round__${scopeKey}`
      : metric === 'stage'
        ? `league_stage_best__${scopeKey}`
        : `league_points_best__${scopeKey}`;
  return {
    recordKey,
    // Deliberately misleading display label: placement must use the
    // structured scopeKey/metric fields, never this text.
    label: 'Totally unrelated display words',
    category: 'League',
    scope: 'Unrelated scope words',
    value: 1000,
    valueDisplay: '1.000',
    isVacant: false,
    holders: [],
    scopeKey,
    metric,
    ...overrides,
  };
}

describe('MSS-140 league scoring grid', () => {
  it('covers nine scopes in backend order with three metric columns', () => {
    assert.deepEqual([...LEAGUE_SCOPE_ORDER], [
      'white',
      'blue',
      'black',
      'red',
      'green',
      'multicolor',
      'hybrid',
      'colorless',
      'superleague',
    ]);
    assert.deepEqual([...LEAGUE_METRIC_ORDER], ['round', 'stage', 'season']);
    const grid = buildLeagueGrid([]);
    assert.equal(grid.length, 9);
    assert.deepEqual(
      grid.map((row) => row.scopeKey),
      [...LEAGUE_SCOPE_ORDER],
    );
    for (const row of grid) {
      assert.equal(row.round, undefined);
      assert.equal(row.stage, undefined);
      assert.equal(row.season, undefined);
    }
  });

  it('places cells from structured scope/metric fields, not display labels', () => {
    const white = leagueRecord('white', 'round');
    const superSeason = leagueRecord('superleague', 'season');
    const grid = buildLeagueGrid([white, superSeason]);
    const whiteRow = grid.find((row) => row.scopeKey === 'white')!;
    const superRow = grid.find((row) => row.scopeKey === 'superleague')!;
    assert.equal(whiteRow.round?.recordKey, 'league_single_round__white');
    assert.equal(whiteRow.stage, undefined);
    assert.equal(whiteRow.season, undefined);
    assert.equal(superRow.season?.recordKey, 'league_points_best__superleague');
    assert.equal(superRow.round, undefined);
    // Display labels differ per record but never affect placement.
    assert.notEqual(white.label, whiteRow.round?.scopeKey);
  });

  it('keeps vacant records in their cell and ignores other categories', () => {
    const vacant = leagueRecord('blue', 'stage', { isVacant: true });
    const cup: ScoringRecord = {
      recordKey: 'colour_cup_individual_round_best',
      label: 'Most athlete points in single Colour Cup individual round',
      category: 'Individual Cups',
      scope: 'Colour Cup individual',
      value: 5,
      valueDisplay: '0.005',
      isVacant: false,
      holders: [],
      scopeKey: 'colour_cup_individual',
      metric: 'round',
    };
    const grid = buildLeagueGrid([vacant, cup]);
    const blue = grid.find((row) => row.scopeKey === 'blue')!;
    assert.equal(blue.stage?.recordKey, 'league_stage_best__blue');
    assert.equal(blue.stage?.isVacant, true);
  });

  it('falls back to stable record keys when additive fields are absent', () => {
    const legacy: ScoringRecord = {
      recordKey: 'league_single_round__red',
      label: 'Most athlete points in single Red League round',
      category: 'League',
      scope: 'Red League',
      value: 7,
      valueDisplay: '0.007',
      isVacant: false,
      holders: [],
    };
    assert.equal(resolveScoringScopeKey(legacy), 'red');
    assert.equal(resolveScoringMetric(legacy), 'round');
    const grid = buildLeagueGrid([legacy]);
    assert.equal(grid.find((row) => row.scopeKey === 'red')?.round?.recordKey, 'league_single_round__red');
  });

  it('labels scopes and metrics without leaking stable keys or Colour spelling', () => {
    assert.equal(leagueScopeDisplayName('white'), 'White League');
    assert.equal(leagueScopeDisplayName('superleague'), 'Superleague');
    assert.equal(leagueMetricDisplayName('round'), 'Single round');
    assert.equal(leagueMetricDisplayName('stage'), 'Single stage');
    assert.equal(leagueMetricDisplayName('season'), 'Season points');
    assert.equal(cupEventDisplayName('colour_cup_individual'), 'Color Cup individual');
    assert.equal(cupEventDisplayName('qualifier'), 'Qualifier');
    assert.equal(cupEventDisplayName('colour_cup_team'), 'Color Cup team');
    assert.equal(cupEventDisplayName('type_cup_team'), 'Type Cup team');
  });
});

describe('MSS-140 cup event groups', () => {
  function cupRecord(recordKey: string, category: 'Individual Cups' | 'Team Cups', event: string, metric: string): ScoringRecord {
    return {
      recordKey,
      label: `${recordKey} label`,
      category,
      scope: event,
      value: 1,
      valueDisplay: '0.001',
      isVacant: false,
      holders: [],
      scopeKey: event,
      metric,
    };
  }

  it('groups individual cups by event in stable order', () => {
    const groups = groupCupRecords(
      [
        cupRecord('qualifier_stage_best', 'Individual Cups', 'qualifier', 'stage'),
        cupRecord('colour_cup_individual_round_best', 'Individual Cups', 'colour_cup_individual', 'round'),
      ],
      'Individual Cups',
    );
    assert.deepEqual(
      groups.map((group) => group.eventKey),
      ['colour_cup_individual', 'qualifier'],
    );
  });

  it('groups team cups by event and drops other categories', () => {
    const leagueStray: ScoringRecord = {
      recordKey: 'league_single_round__white',
      label: 'League stray',
      category: 'League',
      scope: 'White League',
      value: 1,
      valueDisplay: '0.001',
      isVacant: false,
      holders: [],
      scopeKey: 'white',
      metric: 'round',
    };
    const groups = groupCupRecords(
      [
        cupRecord('type_cup_team_total_best', 'Team Cups', 'type_cup_team', 'total'),
        leagueStray,
      ],
      'Team Cups',
    );
    assert.deepEqual(
      groups.map((group) => group.eventKey),
      ['type_cup_team'],
    );
  });
});

describe('MSS-140 career holder preview', () => {
  it('shows about 10 holders with a "+N more" remainder', () => {
    assert.equal(CAREER_HOLDER_PREVIEW_COUNT, 10);
    const holders = Array.from({ length: 18 }, (_, index) => index);
    const { visible, hiddenCount } = splitHolderPreview(holders);
    assert.equal(visible.length, 10);
    assert.equal(hiddenCount, 8);
  });

  it('renders short holder lists fully with no toggle remainder', () => {
    const { visible, hiddenCount } = splitHolderPreview([1, 2, 3]);
    assert.deepEqual(visible, [1, 2, 3]);
    assert.equal(hiddenCount, 0);
    const exact = splitHolderPreview(Array.from({ length: 10 }, (_, index) => index));
    assert.equal(exact.visible.length, 10);
    assert.equal(exact.hiddenCount, 0);
  });
});

describe('MSS-140 records views shell', () => {
  it('backs every view with a shareable URL and an announced switcher', () => {
    const page = read('features/records/RecordsPage.tsx');
    assert.ok(page.includes("import './RecordsPage.css'"), 'records ship their own stylesheet');
    assert.ok(page.includes('recordsPath(saveId'), 'switcher builds shareable URLs');
    assert.ok(page.includes('className="segmented records-switcher"'), 'switcher reuses the segmented control');
    assert.ok(page.includes('aria-current="page"'), 'the current view is announced');
    assert.ok(page.includes("urlView ?? 'fame'"), 'absent views fall back to Hall of Fame');
    assert.ok(page.includes('view === \'fame\''), 'hall of fame view renders');
    assert.ok(page.includes('view === \'career\''), 'career view renders');
    assert.ok(page.includes('view === \'scoring\''), 'scoring view renders');
    assert.ok(page.includes('view === \'honours\''), 'honours view renders');
  });

  it('uses full-width views instead of the cramped auto-fit cards', () => {
    const page = read('features/records/RecordsPage.tsx');
    assert.ok(!page.includes('page-grid'), 'records no longer use the shared auto-fit card grid');
    assert.ok(page.includes('className="records-page"'), 'views share one full-width container');
    assert.ok(page.includes('buildLeagueGrid'), 'league scoring renders the 9x3 grid');
    assert.ok(page.includes('groupCupRecords'), 'cups render grouped by event');
    assert.ok(page.includes('CAREER_HOLDER_PREVIEW_COUNT'), 'career ties collapse behind +N more');
    assert.ok(page.includes('aria-expanded'), '+N more announces its expanded state');
  });

  it('keeps loading, empty and vacant states for every view', () => {
    const page = read('features/records/RecordsPage.tsx');
    assert.ok(page.includes('Loading records…'), 'loading states remain');
    assert.ok(page.includes('Records unavailable'), 'error states remain');
    assert.ok(page.includes('No records yet.'), 'empty states remain');
    assert.ok(page.includes('>Vacant<'), 'vacant cells render');
    assert.ok(page.includes('holder'), 'the total holder count sits next to the value');
  });

  it('keeps every athlete link and info text', () => {
    const page = read('features/records/RecordsPage.tsx');
    assert.ok(page.includes('AthleteLink'), 'athlete links remain');
    assert.ok(page.includes('Where points came from'), 'hall of fame breakdown remains');
    assert.ok(page.includes('Ranking window'), 'hall of fame window selector remains');
    assert.ok(page.includes('Ties share the record'), 'career info text remains');
    assert.ok(page.includes('Single-round maxima decode'), 'league info text remains');
    assert.ok(page.includes('No official honours yet'), 'honours empty text remains');
  });
});
