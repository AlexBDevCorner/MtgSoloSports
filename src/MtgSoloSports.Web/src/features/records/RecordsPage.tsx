import { useEffect, useMemo, useState } from 'react';
import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import { ApiError, apiErrorMessage } from '../../shared/api/http';
import { AthleteLink } from '../routing/router';
import {
  fetchHallOfFame,
  fetchHonours,
  fetchRecords,
  type HallOfFame,
  type HallOfFameLeader,
  type Honours,
  type Records,
  type ScoringRecord,
  type ScoringRecordHolder,
} from './recordsApi';
import { fetchHistorySeasons } from '../history/historyApi';
import { formatScoringContext } from './scoringFormat';
import {
  careerRecordLabel,
  honourKindLabel,
  scoringRecordLabel,
  scoringScopeLabel,
} from '../../shared/displayLabels';

type PeriodMode = 'all' | 'single' | 'range';

interface PeriodSelection {
  mode: PeriodMode;
  season: number | null;
  from: number | null;
  to: number | null;
}

const DEFAULT_PERIOD: PeriodSelection = { mode: 'all', season: null, from: null, to: null };

function periodStorageKey(saveId: string): string {
  return `mtg-solo-sports:hall-of-fame-period:${saveId}`;
}

/**
 * Selected Career Leaders window, preserved per universe in sessionStorage so
 * Back navigation from an athlete profile restores the same ranking without
 * putting period state in the shared route shape. A new universe (new saveId)
 * starts at All time; out-of-range values reconcile to All time once seasons
 * load.
 */
function loadStoredPeriod(saveId: string): PeriodSelection {
  try {
    const raw = sessionStorage.getItem(periodStorageKey(saveId));
    if (!raw) {
      return DEFAULT_PERIOD;
    }
    const parsed = JSON.parse(raw) as Partial<PeriodSelection>;
    if (parsed.mode === 'single' && typeof parsed.season === 'number' && parsed.season >= 1) {
      return { mode: 'single', season: Math.floor(parsed.season), from: null, to: null };
    }
    if (
      parsed.mode === 'range' &&
      typeof parsed.from === 'number' &&
      typeof parsed.to === 'number' &&
      parsed.from >= 1 &&
      parsed.to >= parsed.from
    ) {
      return { mode: 'range', season: null, from: Math.floor(parsed.from), to: Math.floor(parsed.to) };
    }
  } catch {
    // Corrupt stored state falls back to All time; never blocks the page.
  }
  return DEFAULT_PERIOD;
}

function periodLabel(period: PeriodSelection): string {
  if (period.mode === 'single' && period.season !== null) {
    return `Season ${period.season}`;
  }
  if (period.mode === 'range' && period.from !== null && period.to !== null) {
    return period.from === period.to ? `Season ${period.from}` : `Seasons ${period.from}–${period.to}`;
  }
  return 'All time';
}

function periodQuery(period: PeriodSelection): { fromSeason: number | null; toSeason: number | null } {
  if (period.mode === 'single' && period.season !== null) {
    return { fromSeason: period.season, toSeason: period.season };
  }
  if (period.mode === 'range' && period.from !== null && period.to !== null) {
    return { fromSeason: period.from, toSeason: period.to };
  }
  return { fromSeason: null, toSeason: null };
}

/** Compact auditable breakdown of one leader's points for the selected range. */
function LeaderBreakdown({ row }: { row: HallOfFameLeader }) {
  return (
    <details>
      <summary className="card-sub">Where points came from</summary>
      <dl className="card-sub">
        <div>
          <dt>Superleague</dt> <dd>{row.superleaguePoints}</dd>
        </div>
        <div>
          <dt>Feeder 1</dt> <dd>{row.feeder1Points}</dd>
        </div>
        <div>
          <dt>Feeder 2</dt> <dd>{row.feeder2Points}</dd>
        </div>
        <div>
          <dt>Feeder 3</dt> <dd>{row.feeder3Points}</dd>
        </div>
        <div>
          <dt>Color Cup individual</dt> <dd>{row.colorCupIndividualPoints}</dd>
        </div>
        <div>
          <dt>Color Cup team</dt> <dd>{row.colorCupTeamPoints}</dd>
        </div>
        <div>
          <dt>Type Cup team</dt> <dd>{row.typeCupTeamPoints}</dd>
        </div>
        <div>
          <dt>Major wins</dt> <dd>{row.majorWins}</dd>
        </div>
        <div>
          <dt>Scored events</dt> <dd>{row.scoredEvents}</dd>
        </div>
      </dl>
    </details>
  );
}

function ScoringHolders({
  saveId,
  holders,
}: {
  saveId: string;
  holders: ScoringRecordHolder[];
}) {
  return (
    <>
      {holders.map((holder, index) => (
        <div key={`${holder.athleteId ?? holder.teamKey}-${holder.seasonNumber}-${holder.roundNumber ?? '-'}-${holder.stageNumber ?? '-'}-${holder.groupNumber ?? '-'}-${index}`}>
          {holder.athleteId !== null && holder.athleteName ? (
            <AthleteLink saveId={saveId} athleteId={holder.athleteId} name={holder.athleteName} />
          ) : (
            <span>{holder.teamName || holder.teamKey}</span>
          )}
          {holder.athleteId !== null && holder.teamKey ? (
            <span className="card-sub"> ({holder.teamName || holder.teamKey})</span>
          ) : undefined}
          <span className="card-sub"> · {formatScoringContext(holder)}</span>
          {index < holders.length - 1 ? <span>, </span> : undefined}
        </div>
      ))}
    </>
  );
}

function ScoringTable({ saveId, records }: { saveId: string; records: ScoringRecord[] }) {
  if (records.length === 0) {
    return <p className="muted">No records yet.</p>;
  }
  return (
    <div className="table-wrap">
      <table className="data-table">
        <thead>
          <tr>
            <th scope="col">Record</th>
            <th scope="col" className="numeric">Value</th>
            <th scope="col">Holders</th>
          </tr>
        </thead>
        <tbody>
          {records.map((record) => (
            <tr key={record.recordKey}>
              <td>
                {scoringRecordLabel(record.recordKey, record.label)}
                <span className="card-sub"> · {scoringScopeLabel(record.scope)}</span>
              </td>
              <td className="numeric">{record.valueDisplay}</td>
              <td>
                {record.isVacant ? (
                  <span className="muted">Vacant</span>
                ) : (
                  <ScoringHolders saveId={saveId} holders={record.holders} />
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function useSaveData<T>(
  saveId: string | null,
  load: (id: string, signal: AbortSignal) => Promise<T>,
): { data: T | null; loading: boolean; error: string | null } {
  const [data, setData] = useState<T | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!saveId) {
      setData(null);
      setLoading(false);
      setError(null);
      return;
    }
    const controller = new AbortController();
    const { signal } = controller;
    setLoading(true);
    setError(null);
    load(saveId, signal)
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
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [saveId]);

  return { data, loading, error };
}

export function RecordsPage({ saveId }: { saveId: string }) {
  const recordsState = useSaveData<Records>(saveId, (id, signal) =>
    fetchRecords(id, signal),
  );
  const honoursState = useSaveData<Honours>(saveId, (id, signal) =>
    fetchHonours(id, signal),
  );
  const [period, setPeriod] = useState<PeriodSelection>(() => loadStoredPeriod(saveId));
  const [seasons, setSeasons] = useState<number[]>([]);
  const [fame, setFame] = useState<HallOfFame | null>(null);
  const [fameLoading, setFameLoading] = useState(false);
  const [fameError, setFameError] = useState<string | null>(null);

  // Changing universe resets to that universe's stored (or default) window.
  useEffect(() => {
    setPeriod(loadStoredPeriod(saveId));
    setFame(null);
    setFameError(null);
  }, [saveId]);

  useEffect(() => {
    try {
      sessionStorage.setItem(periodStorageKey(saveId), JSON.stringify(period));
    } catch {
      // storage is best-effort; the ranking still works in memory
    }
  }, [saveId, period]);

  useEffect(() => {
    const controller = new AbortController();
    fetchHistorySeasons(saveId, controller.signal)
      .then((loaded) => {
        setSeasons(loaded.seasons.map((season) => season.seasonNumber).sort((a, b) => a - b));
      })
      .catch(() => {
        setSeasons([]);
      });
    return () => {
      controller.abort();
    };
  }, [saveId]);

  const reconciled = useMemo((): PeriodSelection => {
    if (seasons.length === 0) {
      return period;
    }
    if (period.mode === 'single' && period.season !== null) {
      return seasons.includes(period.season) ? period : DEFAULT_PERIOD;
    }
    if (period.mode === 'range' && period.from !== null && period.to !== null) {
      return seasons.includes(period.from) && seasons.includes(period.to)
        ? period
        : DEFAULT_PERIOD;
    }
    return period;
  }, [period, seasons]);

  const query = periodQuery(reconciled);
  const periodInvalid = query.fromSeason !== null && query.toSeason !== null && query.fromSeason > query.toSeason;

  useEffect(() => {
    if (periodInvalid) {
      return;
    }
    const controller = new AbortController();
    setFameLoading(true);
    setFameError(null);
    fetchHallOfFame(saveId, 20, controller.signal, query.fromSeason, query.toSeason)
      .then((loaded) => {
        setFame(loaded);
        setFameLoading(false);
      })
      .catch((failure: unknown) => {
        if (failure instanceof DOMException && failure.name === 'AbortError') {
          return;
        }
        if (failure instanceof ApiError && failure.status === 404) {
          setFame(null);
          setFameError(null);
        } else {
          setFameError(apiErrorMessage(failure));
        }
        setFameLoading(false);
      });
    return () => {
      controller.abort();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [saveId, query.fromSeason, query.toSeason]);

  const loading =
    (recordsState.loading && !recordsState.data) ||
    (fameLoading && !fame);
  if (loading) {
    return <Loading label="Loading records…" />;
  }

  const error = recordsState.error ?? fameError ?? honoursState.error;
  if (error && !recordsState.data && !fame) {
    return (
      <Notice tone="error" title="Records unavailable">
        <p>{error}</p>
      </Notice>
    );
  }

  const records = recordsState.data?.records ?? [];
  const history = recordsState.data?.recentHistory ?? [];
  const scoring = recordsState.data?.scoringRecords ?? [];
  const leaders = fame?.leaders ?? [];
  const honours = honoursState.data?.honours ?? [];
  const leagueScoring = scoring.filter((r) => r.category === 'League');
  const individualScoring = scoring.filter((r) => r.category === 'Individual Cups');
  const teamScoring = scoring.filter((r) => r.category === 'Team Cups');

  return (
    <div className="page-grid">
      <Card
        eyebrow="Hall of Fame"
        title={`Career leaders — ${periodLabel(reconciled)} · ${leaders.length} shown`}
        action={
          fameLoading ? (
            <span className="muted small">Refreshing…</span>
          ) : undefined
        }
        info={
          <p>
            Ordered by career performance points, then major-event wins, Superleague titles,
            Superleague points, then name. Every finalized league and major-cup placement in
            the selected seasons scores; active bonus is never part of the score.
          </p>
        }
      >
        <div className="card-filters" role="group" aria-label="Career leaders period">
          <label>
            Window{' '}
            <select
              aria-label="Ranking window"
              value={reconciled.mode}
              onChange={(event) => {
                const mode = event.target.value as PeriodMode;
                if (mode === 'all') {
                  setPeriod(DEFAULT_PERIOD);
                } else if (mode === 'single') {
                  const fallback = seasons[seasons.length - 1] ?? 1;
                  setPeriod({ mode: 'single', season: reconciled.season ?? fallback, from: null, to: null });
                } else {
                  const fallback = seasons[seasons.length - 1] ?? 1;
                  setPeriod({
                    mode: 'range',
                    season: null,
                    from: reconciled.from ?? seasons[0] ?? 1,
                    to: reconciled.to ?? fallback,
                  });
                }
              }}
            >
              <option value="all">All time</option>
              <option value="single">Single season</option>
              <option value="range">Custom range</option>
            </select>
          </label>
          {reconciled.mode === 'single' ? (
            <label>
              Season{' '}
              <select
                aria-label="Season"
                value={reconciled.season ?? ''}
                onChange={(event) => {
                  setPeriod({ mode: 'single', season: Number(event.target.value), from: null, to: null });
                }}
              >
                {seasons.map((season) => (
                  <option key={season} value={season}>
                    Season {season}
                  </option>
                ))}
              </select>
            </label>
          ) : undefined}
          {reconciled.mode === 'range' ? (
            <>
              <label>
                From{' '}
                <select
                  aria-label="From season"
                  value={reconciled.from ?? ''}
                  onChange={(event) => {
                    setPeriod({ mode: 'range', season: null, from: Number(event.target.value), to: reconciled.to });
                  }}
                >
                  {seasons.map((season) => (
                    <option key={season} value={season}>
                      Season {season}
                    </option>
                  ))}
                </select>
              </label>
              <label>
                To{' '}
                <select
                  aria-label="To season"
                  value={reconciled.to ?? ''}
                  onChange={(event) => {
                    setPeriod({ mode: 'range', season: null, from: reconciled.from, to: Number(event.target.value) });
                  }}
                >
                  {seasons.map((season) => (
                    <option key={season} value={season}>
                      Season {season}
                    </option>
                  ))}
                </select>
              </label>
            </>
          ) : undefined}
        </div>
        {periodInvalid ? (
          <Notice tone="error" title="Invalid season range">
            <p>From season must not exceed To season.</p>
          </Notice>
        ) : leaders.length === 0 ? (
          <p className="muted">
            No leaders yet for this window. Complete a season to persist official results.
          </p>
        ) : (
          <div className="table-wrap">
            <table className="data-table">
              <thead>
                <tr>
                    <th scope="col" className="numeric">Rank</th>
                    <th scope="col">Card</th>
                    <th scope="col" className="numeric">Career points</th>
                    <th scope="col" className="numeric">Titles</th>
                    <th scope="col">Breakdown</th>
                </tr>
              </thead>
              <tbody>
                {leaders.map((row) => (
                  <tr key={row.athleteId}>
                    <td className="numeric">{row.rank}</td>
                    <td>
                      <AthleteLink saveId={saveId} athleteId={row.athleteId} name={row.athleteName} />
                      <span className="card-sub">
                        {' '}
                        · {row.sportingColorName}
                        {row.superleagueTitles > 0
                          ? ` · ${row.superleagueTitles} SL`
                          : ''}
                      </span>
                    </td>
                    <td className="numeric">{row.careerPoints}</td>
                    <td className="numeric">
                      {row.totalTitles} ({row.feederTitles}F · {row.superleagueTitles}S)
                    </td>
                    <td>
                      <LeaderBreakdown row={row} />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>

      <Card
        eyebrow="Records"
        title={`Career records — ${records.length}`}
        info={
          <p>
            Ties share the record; only an outright higher value replaces holders and emits a
            new-record story.
          </p>
        }
      >
        {records.length === 0 ? (
          <p className="muted">No records yet.</p>
        ) : (
          <div className="table-wrap">
            <table className="data-table">
              <thead>
                <tr>
                    <th scope="col">Record</th>
                    <th scope="col" className="numeric">Value</th>
                    <th scope="col">Holders</th>
                </tr>
              </thead>
              <tbody>
                {records.map((record) => (
                  <tr key={record.recordKey}>
                    <td>{careerRecordLabel(record.recordKey, record.label)}</td>
                    <td className="numeric">{record.valueDisplay}</td>
                    <td>
                      {record.isVacant ? (
                        <span className="muted">Vacant</span>
                      ) : (
                        record.holders.map((holder, index) => (
                          <span key={holder.athleteId}>
                            {index > 0 ? ', ' : ''}
                            <AthleteLink saveId={saveId} athleteId={holder.athleteId} name={holder.athleteName} />
                          </span>
                        ))
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>

      <Card
        eyebrow="Scoring records"
        title={`League scoring — ${leagueScoring.length}`}
        info={
          <p>
            Single-round maxima decode immutable round payloads; stage and league-point
            totals use normalized standings. Records stay scoped per league so feeder
            colors and the Superleague never mix. Ties share the record with season,
            league, stage and round context; historical ownership never rewrites when
            athletes change league or team.
          </p>
        }
      >
        <ScoringTable saveId={saveId} records={leagueScoring} />
      </Card>

      <Card
        eyebrow="Scoring records"
        title={`Individual Cups — ${individualScoring.length}`}
        info={
            <p>
              Color Cup individual and Qualifier scoring, separated by event type and
              round/stage scope. Values come from persisted Cup and Qualifier results so
              old seasons keep contributing after new seasons start.
            </p>
        }
      >
        <ScoringTable saveId={saveId} records={individualScoring} />
      </Card>

      <Card
        eyebrow="Scoring records"
        title={`Team Cups — ${teamScoring.length}`}
        info={
            <p>
              Color Cup and Type Cup team scoring, separated by event type and
              leg/group-stage scope. Individual leg rounds, leg group-stage totals, team
              single-round totals and full team-event totals are tracked separately.
            </p>
        }
      >
        <ScoringTable saveId={saveId} records={teamScoring} />
      </Card>

      <Card eyebrow="History" title={`Record breaks — ${history.length} recent`}>
        {history.length === 0 ? (
          <p className="muted">
            No record breaks yet. New outright records appear here after season
            finalization.
          </p>
        ) : (
          <ul className="story-list">
            {history.map((item, index) => (
              <li key={`${item.recordKey}-${item.athleteId}-${item.seasonNumber}-${index}`}>
                <span className="badge badge-ready">{careerRecordLabel(item.recordKey)}</span>{' '}
                <AthleteLink saveId={saveId} athleteId={item.athleteId} name={item.athleteName} />{' '}
                <span>{item.text}</span>
              </li>
            ))}
          </ul>
        )}
      </Card>

      <Card eyebrow="Honours" title={`Official honours — ${honours.length}`}>
        {honoursState.loading && honours.length === 0 ? (
          <Loading label="Loading honours…" />
        ) : honours.length === 0 ? (
          <p className="muted">
            No official honours yet. Feeder and Superleague podiums (1st/2nd/3rd) plus Color Cup
            and Type Cup podiums persist at season finalization.
          </p>
        ) : (
          <div className="table-wrap">
            <table className="data-table">
              <thead>
                <tr>
                    <th scope="col" className="numeric">Season</th>
                    <th scope="col">League</th>
                    <th scope="col">Athlete</th>
                    <th scope="col">Honour</th>
                </tr>
              </thead>
              <tbody>
                {honours.map((row) => (
                  <tr key={`${row.seasonNumber}-${row.leagueId}-${row.athleteId}-${row.honourKind}`}>
                    <td className="numeric">{row.seasonNumber}</td>
                    <td>{row.leagueName}</td>
                    <td>
                      <AthleteLink saveId={saveId} athleteId={row.athleteId} name={row.athleteName} />
                    </td>
                    <td>{honourKindLabel(row.honourKind)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>
    </div>
  );
}
