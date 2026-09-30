import { useEffect, useMemo, useState } from 'react';
import { Card } from '../../shared/ui/Card';
import { InfoDisclosure } from '../../shared/ui/InfoDisclosure';
import { Loading, Notice } from '../../shared/ui/Notice';
import { ApiError, apiErrorMessage } from '../../shared/api/http';
import { fetchCurrentStandings, type CurrentStandings } from '../athletes/athleteApi';
import {
  fetchHistoryCompetitions,
  fetchHistorySeasonTable,
  fetchHistorySeasons,
  fetchHistoryStages,
  type HistoryCompetitions,
  type HistorySeasons,
  type HistorySeasonTable,
  type HistoryStages,
} from '../history/historyApi';
import { AthleteLink, Link } from '../routing/router';
import { historyPath, livePath, savesPath, type StandingsView } from '../routing/routes';
import { zoneLabelForRank } from './zones';
import { fetchSeasonPlacements, type SeasonPlacements } from './standingsApi';
import {
  allStageColumns,
  buildCellMap,
  cellTitle,
  formatBonus,
  formatPoints,
  maxCompletedStage,
  placeLabel,
  podiumClass,
  sortMatrixRows,
  visibleStageColumns,
  type CombinedMatrixRow,
  type MatrixSortKey,
  type StageWindow,
} from './matrix';
import './StandingsPage.css';

function useAsync<T>(
  key: string | null,
  load: (signal: AbortSignal) => Promise<T>,
): { data: T | null; loading: boolean; error: string | null } {
  const [data, setData] = useState<T | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!key) {
      setData(null);
      setLoading(false);
      setError(null);
      return;
    }
    const controller = new AbortController();
    const { signal } = controller;
    setLoading(true);
    setError(null);
    load(signal)
      .then((loaded) => {
        setData(loaded);
        setLoading(false);
      })
      .catch((failure: unknown) => {
        if (failure instanceof DOMException && failure.name === 'AbortError') {
          return;
        }
        if (failure instanceof ApiError && failure.status === 404) {
          setData(null);
          setError(null);
        } else {
          setError(apiErrorMessage(failure));
        }
        setLoading(false);
      });
    return () => {
      controller.abort();
    };
    // load is stable per key via inline closure over ids; re-run on key change only.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key]);

  return { data, loading, error };
}

export interface StandingsSelection {
  league: number | null;
  season: number | null;
  view: StandingsView | null;
}

interface TotalsRow {
  athleteId: number;
  name: string;
  seasonRank: number;
  totalChampionshipPointsThousandths: number;
  totalStageScoreThousandths: number;
  roundWins: number;
  stageWins: number;
  isChampion: boolean;
}

export function StandingsPage({
  saveId,
  urlLeagueId,
  urlSeason,
  urlView,
  onStandingsChange,
}: {
  saveId: string;
  urlLeagueId: number | null;
  urlSeason: number | null;
  urlView: StandingsView | null;
  onStandingsChange: (selection: StandingsSelection) => void;
}) {
  const [seasonNumber, setSeasonNumber] = useState<number | null>(urlSeason);
  const [leagueId, setLeagueId] = useState<number | null>(urlLeagueId);
  const [view, setView] = useState<StandingsView>(urlView ?? 'season');
  const [sortKey, setSortKey] = useState<MatrixSortKey>('rank');
  const [showBonus, setShowBonus] = useState(false);
  const [stageWindow, setStageWindow] = useState<StageWindow>('all');

  useEffect(() => {
    setSeasonNumber(urlSeason);
  }, [urlSeason, saveId]);
  useEffect(() => {
    setLeagueId(urlLeagueId);
  }, [urlLeagueId, saveId]);
  useEffect(() => {
    setView(urlView ?? 'season');
  }, [urlView, saveId]);

  const seasonsState = useAsync<HistorySeasons>(saveId, (signal) =>
    fetchHistorySeasons(saveId as string, signal),
  );
  const seasons = seasonsState.data?.seasons ?? [];
  const latestSeason = seasons.length > 0 ? seasons[seasons.length - 1]!.seasonNumber : null;

  const explicitSeasonInvalid =
    urlSeason !== null && seasons.length > 0 && !seasons.some((row) => row.seasonNumber === urlSeason);

  useEffect(() => {
    if (seasons.length === 0) {
      return;
    }
    setSeasonNumber((current) => {
      if (urlSeason !== null && seasons.some((row) => row.seasonNumber === urlSeason)) {
        return urlSeason;
      }
      if (urlSeason !== null) {
        return current;
      }
      if (current !== null && seasons.some((row) => row.seasonNumber === current)) {
        return current;
      }
      return seasons[seasons.length - 1]!.seasonNumber;
    });
  }, [seasons, urlSeason]);

  const competitionsKey =
    saveId && seasonNumber !== null && !explicitSeasonInvalid
      ? `${saveId}/standings-s${seasonNumber}`
      : null;
  const competitionsState = useAsync<HistoryCompetitions>(competitionsKey, (signal) =>
    fetchHistoryCompetitions(saveId as string, seasonNumber as number, signal),
  );
  const competitions = competitionsState.data?.competitions ?? [];
  const explicitLeagueInvalid =
    urlLeagueId !== null &&
    competitions.length > 0 &&
    !competitions.some((row) => row.leagueId === urlLeagueId);

  useEffect(() => {
    if (competitions.length === 0) {
      return;
    }
    setLeagueId((current) => {
      if (urlLeagueId !== null && competitions.some((row) => row.leagueId === urlLeagueId)) {
        return urlLeagueId;
      }
      if (urlLeagueId !== null) {
        return current;
      }
      if (current !== null && competitions.some((row) => row.leagueId === current)) {
        return current;
      }
      return [...competitions].sort((a, b) => a.leagueId - b.leagueId)[0]!.leagueId;
    });
  }, [competitions, urlLeagueId]);

  const leagueKnown = leagueId !== null && competitions.some((row) => row.leagueId === leagueId);
  const seasonEntry = seasons.find((row) => row.seasonNumber === seasonNumber) ?? null;
  const isLatestSeason = seasonNumber !== null && seasonNumber === latestSeason;
  const useCurrentTotals =
    seasonEntry !== null && !seasonEntry.isComplete && isLatestSeason && leagueKnown;

  const stagesKey =
    saveId && seasonNumber !== null && leagueId !== null && leagueKnown && !explicitSeasonInvalid
      ? `${saveId}/standings-stages-s${seasonNumber}-l${leagueId}`
      : null;
  const stagesState = useAsync<HistoryStages>(stagesKey, (signal) =>
    fetchHistoryStages(saveId as string, seasonNumber as number, leagueId as number, signal),
  );

  const placementsKey =
    saveId && seasonNumber !== null && leagueId !== null && leagueKnown && !explicitSeasonInvalid
      ? `${saveId}/standings-placements-s${seasonNumber}-l${leagueId}`
      : null;
  const placementsState = useAsync<SeasonPlacements>(placementsKey, (signal) =>
    fetchSeasonPlacements(saveId as string, seasonNumber as number, leagueId as number, signal),
  );

  const historyTableKey =
    saveId &&
    seasonNumber !== null &&
    leagueId !== null &&
    leagueKnown &&
    !explicitSeasonInvalid &&
    seasonEntry !== null &&
    (seasonEntry.isComplete || !isLatestSeason)
      ? `${saveId}/standings-table-s${seasonNumber}-l${leagueId}`
      : null;
  const historyTableState = useAsync<HistorySeasonTable>(historyTableKey, (signal) =>
    fetchHistorySeasonTable(saveId as string, seasonNumber as number, leagueId as number, signal),
  );

  const currentTotalsKey =
    saveId && useCurrentTotals && leagueId !== null
      ? `${saveId}/standings-current-l${leagueId}`
      : null;
  const currentTotalsState = useAsync<CurrentStandings>(currentTotalsKey, (signal) =>
    fetchCurrentStandings(saveId as string, leagueId as number, signal),
  );

  function pushSelection(next: StandingsSelection): void {
    onStandingsChange(next);
  }

  const placements = placementsState.data;
  const stages = stagesState.data?.stages ?? [];
  const leagueKind =
    placements?.leagueKind ??
    competitions.find((row) => row.leagueId === leagueId)?.kind ??
    'Feeder';

  const totalsRows: TotalsRow[] = useMemo(() => {
    if (useCurrentTotals) {
      const current = currentTotalsState.data;
      if (!current || current.standings.length === 0) {
        return [];
      }
      return current.standings.map((row) => ({
        athleteId: row.athleteId,
        name: row.name,
        seasonRank: row.seasonRank,
        totalChampionshipPointsThousandths: row.totalChampionshipPointsThousandths,
        totalStageScoreThousandths: row.totalStageScoreThousandths,
        roundWins: row.roundWins,
        stageWins: row.stageWins,
        isChampion: row.isChampion,
      }));
    }
    const table = historyTableState.data;
    if (!table || table.standings.length === 0) {
      return [];
    }
    return table.standings.map((row) => ({
      athleteId: row.athleteId,
      name: row.name,
      seasonRank: row.seasonRank,
      totalChampionshipPointsThousandths: row.totalChampionshipPointsThousandths,
      totalStageScoreThousandths: row.totalStageScoreThousandths,
      roundWins: row.roundWins,
      stageWins: row.stageWins,
      isChampion: row.isChampion,
    }));
  }, [useCurrentTotals, currentTotalsState.data, historyTableState.data]);

  const totalsByAthlete = useMemo(() => {
    const map = new Map<number, TotalsRow>();
    for (const row of totalsRows) {
      map.set(row.athleteId, row);
    }
    return map;
  }, [totalsRows]);

  const combinedRows: CombinedMatrixRow[] = useMemo(() => {
    if (!placements || placements.athletes.length === 0) {
      return [];
    }
    return placements.athletes.map((athlete) => {
      const totals = totalsByAthlete.get(athlete.athleteId);
      return {
        athleteId: athlete.athleteId,
        name: totals?.name ?? athlete.name,
        seasonRank: totals?.seasonRank ?? null,
        totalChampionshipPointsThousandths: totals?.totalChampionshipPointsThousandths ?? null,
        stageWins: totals?.stageWins ?? (placements.completedStages === 0 ? 0 : null),
        roundWins: totals?.roundWins ?? null,
        sportingColorName: athlete.sportingColorName,
        imageUrl: athlete.imageUrl,
        currentEffectiveBonusThousandths: athlete.currentEffectiveBonusThousandths,
      };
    });
  }, [placements, totalsByAthlete]);

  const sortedRows = useMemo(() => sortMatrixRows(combinedRows, sortKey), [combinedRows, sortKey]);

  const cellMap = useMemo(
    () => buildCellMap(placements?.placements ?? []),
    [placements],
  );

  const stageColumns = useMemo(() => {
    if (stageWindow === 'completed') {
      const completed = placements?.placements ?? [];
      const { length } = visibleStageColumns('completed', completed);
      if (length === 0) {
        return allStageColumns();
      }
    }
    return visibleStageColumns(stageWindow, placements?.placements ?? []);
  }, [stageWindow, placements]);

  const completedStages = placements?.completedStages ?? 0;
  const lastCompletedStage = maxCompletedStage(placements?.placements ?? []);
  const inProgressStage = stages.find((row) => !row.isComplete && row.completedRounds > 0) ?? null;
  const totalsLoading = useCurrentTotals ? currentTotalsState.loading : historyTableState.loading;
  const totalsError = useCurrentTotals ? currentTotalsState.error : historyTableState.error;

  if (seasonsState.loading && seasons.length === 0) {
    return <Loading label="Loading standings…" />;
  }

  if (seasonsState.error) {
    return (
      <Notice tone="error" title="Standings unavailable">
        <p>{seasonsState.error}</p>
        <p>
          <Link to={savesPath()} className="ghost-button">
            Back to saves
          </Link>
        </p>
      </Notice>
    );
  }

  if (seasons.length === 0) {
    return (
      <Notice tone="empty" title="No seasons yet">
        <p>This save has no persisted seasons to rank.</p>
      </Notice>
    );
  }

  if (explicitSeasonInvalid) {
    return (
      <Notice tone="error" title="Season not found">
        <p>
          Season {urlSeason} does not exist in this save. Pick an available season below; no
          other season was substituted.
        </p>
        <p className="live-buttons">
          <Link to={savesPath()} className="ghost-button">
            Back to saves
          </Link>
        </p>
      </Notice>
    );
  }

  if (explicitLeagueInvalid) {
    return (
      <Notice tone="error" title="League not part of this season">
        <p>
          League {urlLeagueId} is not part of Season {seasonNumber}. No other league was
          substituted; pick a league from this season below.
        </p>
      </Notice>
    );
  }

  const seasonOptions = [...seasons].sort((a, b) => a.seasonNumber - b.seasonNumber);
  const leagueOptions = [...competitions].sort((a, b) => a.leagueId - b.leagueId);

  return (
    <div className="dashboard standings-page">
      <div className="toolbar" role="group" aria-label="Standings filters">
        <label className="field">
          <span>League</span>
          <select
            value={leagueId ?? ''}
            disabled={leagueOptions.length === 0}
            onChange={(event) => {
              const next = Number.parseInt(event.target.value, 10);
              const league = Number.isNaN(next) ? null : next;
              setLeagueId(league);
              pushSelection({ league, season: seasonNumber, view });
            }}
          >
            {leagueOptions.map((row) => (
              <option key={row.leagueId} value={row.leagueId}>
                {row.name}
                {row.kind === 'Superleague' ? ' · Superleague' : ''}
              </option>
            ))}
          </select>
        </label>
        <label className="field">
          <span>Season</span>
          <select
            value={seasonNumber ?? ''}
            onChange={(event) => {
              const next = Number.parseInt(event.target.value, 10);
              const season = Number.isNaN(next) ? null : next;
              setSeasonNumber(season);
              setLeagueId(null);
              pushSelection({ league: null, season, view });
            }}
          >
            {seasonOptions.map((row) => (
              <option key={row.seasonNumber} value={row.seasonNumber}>
                Season {row.seasonNumber}
                {row.isComplete ? '' : ' (in progress)'}
              </option>
            ))}
          </select>
        </label>
        <div className="field">
          <span>View</span>
          <div className="segmented" role="group" aria-label="Table views">
            <button
              type="button"
              className={view === 'season' ? 'nav-item current' : 'nav-item'}
              aria-pressed={view === 'season'}
              onClick={() => {
                setView('season');
                pushSelection({ league: leagueId, season: seasonNumber, view: 'season' });
              }}
            >
              Season table
            </button>
            <button
              type="button"
              className={view === 'matrix' ? 'nav-item current' : 'nav-item'}
              aria-pressed={view === 'matrix'}
              onClick={() => {
                setView('matrix');
                pushSelection({ league: leagueId, season: seasonNumber, view: 'matrix' });
              }}
            >
              Stage placements
            </button>
          </div>
        </div>
        <label className="field">
          <span>Sort rows by</span>
          <select
            value={sortKey}
            onChange={(event) => {
              const next = event.target.value;
              setSortKey(next === 'wins' || next === 'points' ? next : 'rank');
            }}
          >
            <option value="rank">Season rank</option>
            <option value="wins">Stage wins</option>
            <option value="points">Championship points</option>
          </select>
        </label>
        <div className="toolbar-end">
          {seasonsState.loading || competitionsState.loading ? (
            <span className="muted small">Refreshing…</span>
          ) : null}
          {leagueId !== null && seasonNumber !== null ? (
            <>
              <Link
                to={historyPath(saveId, { season: seasonNumber, competition: leagueId })}
                className="ghost-button"
              >
                History
              </Link>
              <Link to={livePath(saveId, { league: leagueId })} className="ghost-button">
                Live
              </Link>
            </>
          ) : null}
          <InfoDisclosure>
            <p>
              League, season and view are reflected in the URL (`?league=&amp;season=&amp;view=`
              or `/leagues/:leagueId/standings`) so the table can be copied or opened in another
              tab. Each tab keeps its own save context.
            </p>
          </InfoDisclosure>
        </div>
      </div>
      {competitionsState.error ? (
        <p className="muted small">Leagues: {competitionsState.error}</p>
      ) : null}

      <Card
        eyebrow="Progress"
        title={
          placements
            ? `${placements.leagueName} — Season ${placements.seasonNumber} · ${completedStages}/32 stages`
            : 'Season progress'
        }
      >
        {placementsState.loading && !placements ? (
          <Loading label="Loading league table…" />
        ) : placementsState.error ? (
          <Notice tone="error" title="League table unavailable">
            <p>{placementsState.error}</p>
          </Notice>
        ) : !placements ? (
          <Notice tone="empty" title="No league selected">
            <p>Pick a league and season to inspect its table.</p>
          </Notice>
        ) : (
          <>
            <dl className="stats">
              <div>
                <dt>Completed stages</dt>
                <dd>
                  {completedStages} / 32
                  {lastCompletedStage !== null ? ` · updated after Stage ${lastCompletedStage}` : ''}
                </dd>
              </div>
              <div>
                <dt>Season state</dt>
                <dd>{placements.isSeasonComplete ? 'Complete · final table' : 'In progress · live table'}</dd>
              </div>
              <div>
                <dt>In-progress stage</dt>
                <dd>
                  {inProgressStage
                    ? `Stage ${inProgressStage.stageNumber} (${inProgressStage.completedRounds}/16 rounds) · not shown`
                    : 'None · only completed stages appear'}
                </dd>
              </div>
            </dl>
            <p className="muted small">
              Tables accumulate persisted stage data only. An incomplete stage never
              contributes placements, points or bonus to either view.
            </p>
          </>
        )}
      </Card>

      {view === 'season' ? (
        <Card
          eyebrow="Season table"
          title={
            placements
              ? `${placements.leagueName} — Season ${placements.seasonNumber}${placements.isSeasonComplete ? ' · final' : ''}`
              : 'Season table'
          }
        >
          {totalsLoading && totalsRows.length === 0 ? (
            <Loading label="Loading season table…" />
          ) : totalsError ? (
            <Notice tone="error" title="Season table unavailable">
              <p>{totalsError}</p>
            </Notice>
          ) : totalsRows.length === 0 ? (
            <Notice tone="empty" title="No standings yet">
              <p>
                Complete a stage on the Live tab to populate championship standings. Opening
                this page never requires finishing the running stage.
              </p>
            </Notice>
          ) : (
            <>
              <div className="table-wrap">
                <table className="data-table">
                  <thead>
                    <tr>
                      <th scope="col">Rank</th>
                      <th scope="col">Card</th>
                      <th scope="col">Zone</th>
                      <th scope="col">Champ pts</th>
                      <th scope="col">Stage W</th>
                      <th scope="col">Round W</th>
                      <th scope="col">Stage score</th>
                    </tr>
                  </thead>
                  <tbody>
                    {sortedRows.map((row) => {
                      const totals = totalsByAthlete.get(row.athleteId);
                      if (!totals) {
                        return null;
                      }
                      return (
                        <tr key={row.athleteId}>
                          <td className="numeric">{totals.seasonRank}</td>
                          <td>
                            <div className="card-cell">
                              {row.imageUrl ? (
                                <img className="card-thumb" src={row.imageUrl} alt="" loading="lazy" />
                              ) : (
                                <span className="card-thumb card-thumb-fallback" aria-hidden="true">
                                  {row.name.slice(0, 2).toUpperCase()}
                                </span>
                              )}
                              <span className="card-identity">
                                <AthleteLink saveId={saveId} athleteId={row.athleteId} name={row.name} />
                                {totals.isChampion ? <span className="card-sub"> · Champion</span> : null}
                              </span>
                            </div>
                          </td>
                          <td>
                            <span className="badge badge-wait" title="Visual zone only; no quotas applied.">
                              {zoneLabelForRank(totals.seasonRank, leagueKind)}
                            </span>
                          </td>
                          <td className="numeric">{formatPoints(totals.totalChampionshipPointsThousandths)}</td>
                          <td className="numeric">{totals.stageWins}</td>
                          <td className="numeric">{totals.roundWins}</td>
                          <td className="numeric">{formatPoints(totals.totalStageScoreThousandths)}</td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
              <p className="muted small">
                {useCurrentTotals
                  ? 'Live cumulative table from persisted completed stages; final ranks persist at season end.'
                  : 'Persisted final table; completed seasons never change when new stages run.'}{' '}
                Zones are visual only: Superleague 1–16 safe, 17–24 qualifier, 25–32
                relegated; feeders champion auto-promoted plus 2–4 qualifier.
              </p>
            </>
          )}
        </Card>
      ) : (
        <Card
          eyebrow="Stage placements · bonus calibration"
          title={
            placements
              ? `${placements.leagueName} — Season ${placements.seasonNumber} · P1–P32 per completed stage`
              : 'Stage placements'
          }
        >
          {placementsState.loading && !placements ? (
            <Loading label="Loading placements…" />
          ) : placementsState.error ? (
            <Notice tone="error" title="Placements unavailable">
              <p>{placementsState.error}</p>
            </Notice>
          ) : !placements || placements.athletes.length === 0 ? (
            <Notice tone="empty" title="No roster yet">
              <p>This league has no season roster to align.</p>
            </Notice>
          ) : (
            <>
              <div className="live-controls standings-matrix-controls">
                <label className="field">
                  <span>Stage window</span>
                  <select
                    value={stageWindow}
                    onChange={(event) => {
                      const next = event.target.value as StageWindow;
                      setStageWindow(
                        next === 'completed' ||
                        next === '1-8' ||
                        next === '9-16' ||
                        next === '17-24' ||
                        next === '25-32'
                          ? next
                          : 'all',
                      );
                    }}
                  >
                    <option value="all">All 32 stages</option>
                    <option value="completed">Completed only</option>
                    <option value="1-8">Stages 1–8</option>
                    <option value="9-16">Stages 9–16</option>
                    <option value="17-24">Stages 17–24</option>
                    <option value="25-32">Stages 25–32</option>
                  </select>
                </label>
                <label className="check-row">
                  <input
                    type="checkbox"
                    checked={showBonus}
                    onChange={(event) => setShowBonus(event.target.checked)}
                  />
                  Show earned bonus in cells
                </label>
              </div>
              {completedStages === 0 ? (
                <Notice tone="empty" title="No completed stages yet">
                  <p>
                    All 32 stage columns are blank until Stage 1 persists. The 32-athlete
                    roster below stays aligned to this season and league.
                  </p>
                </Notice>
              ) : null}
              <div className="matrix-wrap" role="region" aria-label="Stage placement matrix" tabIndex={0}>
                <table className="data-table matrix-table">
                  <thead>
                    <tr>
                      <th scope="col" className="sticky sticky-rank">
                        Rank
                      </th>
                      <th scope="col" className="sticky sticky-athlete">
                        Card
                      </th>
                      <th scope="col" className="sticky sticky-points">
                        Champ pts
                      </th>
                      <th scope="col" className="sticky sticky-wins">
                        Stage W
                      </th>
                      <th scope="col" className="sticky sticky-bonus" title="Time-dependent current effective bonus, not the bonus used in earlier stages.">
                        Bonus now*
                      </th>
                      {stageColumns.map((stage) => (
                        <th key={stage} scope="col" className="matrix-stage-head" title={`Stage ${stage}${cellMap.size > 0 && [...cellMap.values()].some((byStage) => byStage.has(stage)) ? ' · completed' : ' · not yet completed'}`}>
                          S{stage}
                        </th>
                      ))}
                    </tr>
                  </thead>
                  <tbody>
                    {sortedRows.map((row) => (
                      <tr key={row.athleteId}>
                        <td className="numeric sticky sticky-rank">
                          {row.seasonRank ?? '—'}
                        </td>
                        <td className="sticky sticky-athlete">
                          <div className="card-cell">
                            {row.imageUrl ? (
                              <img className="card-thumb" src={row.imageUrl} alt="" loading="lazy" />
                            ) : (
                              <span className="card-thumb card-thumb-fallback" aria-hidden="true">
                                {row.name.slice(0, 2).toUpperCase()}
                              </span>
                            )}
                            <span className="card-identity">
                              <AthleteLink saveId={saveId} athleteId={row.athleteId} name={row.name} />
                              <span className="card-sub">{row.sportingColorName}</span>
                            </span>
                          </div>
                        </td>
                        <td className="numeric sticky sticky-points">
                          {row.totalChampionshipPointsThousandths !== null
                            ? formatPoints(row.totalChampionshipPointsThousandths)
                            : '—'}
                        </td>
                        <td className="numeric sticky sticky-wins">{row.stageWins ?? '—'}</td>
                        <td
                          className="numeric sticky sticky-bonus"
                          title="Current effective bonus right now; bonus earned in a stage activates only from the next stage."
                        >
                          {formatBonus(row.currentEffectiveBonusThousandths)}
                        </td>
                        {stageColumns.map((stage) => {
                          const cell = cellMap.get(row.athleteId)?.get(stage);
                          const label = cell ? placeLabel(cell.stageRank) : '—';
                          return (
                            <td
                              key={stage}
                              className={`numeric matrix-cell ${podiumClass(cell?.stageRank ?? null)}`}
                              title={cellTitle(row.name, stage, cell)}
                              aria-label={cellTitle(row.name, stage, cell)}
                            >
                              <span className="matrix-place">{label}</span>
                              {showBonus && cell ? (
                                <span className="matrix-bonus">{formatBonus(cell.earnedBonusThousandths)}</span>
                              ) : null}
                            </td>
                          );
                        })}
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              <details className="advanced">
                <summary>Legend · placements, points and bonus activation</summary>
                <ul className="muted small">
                  <li>
                    <strong>P1–P32</strong> is the creature&apos;s actual finishing place in that
                    completed stage, not its running season position. Blank means not yet
                    completed, never zero.
                  </li>
                  <li>
                    <strong>P1 ★ gold</strong>, <strong>P2–P3 silver/bronze</strong> and{' '}
                    <strong>P4–P10</strong> shading are scanning aids only; every cell also
                    carries its text label for non-color access.
                  </li>
                  <li>
                    <strong>Champ pts</strong> are cumulative season championship points from
                    persisted stage results (32-position table, no bonus multiplier).
                  </li>
                  <li>
                    Hover or focus a cell for that stage&apos;s earned bonus and championship
                    points. Earned bonus becomes active only from the <em>next</em> stage;
                    Stage 32 bonus first applies next season at decay weight.
                  </li>
                  <li>
                    <strong>Bonus now*</strong> is the time-dependent current effective bonus
                    and was not retroactively active in earlier stages. Bonus changes future
                    round point value, never shuffle probability.
                  </li>
                </ul>
              </details>
            </>
          )}
        </Card>
      )}
    </div>
  );
}
