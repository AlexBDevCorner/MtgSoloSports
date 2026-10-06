import { useEffect, useMemo, useState } from 'react';
import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import { ApiError, apiErrorMessage } from '../../shared/api/http';
import { runEventRemaining } from '../events/eventsApi';
import { fetchHistorySeasons, type HistorySeasons } from '../history/historyApi';
import { AthleteLink, Link } from '../routing/router';
import { dashboardPath, livePath, qualifierPath, qualifiersPath } from '../routing/routes';
import {
  fetchSaveDetail,
  fetchSeasonProgress,
  fetchSeasonStatus,
  type SeasonProgress,
  type SeasonStatus,
} from '../dashboard/dashboardApi';
import type { SaveDetail } from '../saves/savesApi';
import {
  buildQualifierOverview,
  qualifierFieldRows,
  qualifierOutcomeLabel,
  qualifierProgressLine,
  qualifierRoleLabel,
  type QualifierEntry,
} from './qualifierModel';
import {
  fetchQualifierList,
  runFeederQualifier,
  runRemainingQualifiers,
  type QualifierEvent,
  type QualifierList,
} from './qualifierApi';
import { qualifierBoundarySegment } from '../../shared/leagueTiers';

function formatPoints(thousandths: number): string {
  return (thousandths / 1000).toFixed(3);
}

function useAsync<T>(
  key: string | null,
  load: (signal: AbortSignal) => Promise<T>,
): { data: T | null; loading: boolean; error: string | null; refresh: () => void } {
  const [data, setData] = useState<T | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [revision, setRevision] = useState(0);

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
  }, [key, revision]);

  return { data, loading, error, refresh: () => setRevision((value) => value + 1) };
}

export interface QualifiersSelection {
  season: number | null;
}

/**
 * Multi-qualifier overview (MSS-060): one compact board for the whole
 * qualifier phase instead of 17 unrelated season steps. Completion state
 * comes from persisted qualifier events; pending entries stay visible in
 * canonical backend order. Running qualifiers happens in place — no
 * Dashboard round-trip required.
 */
export function QualifiersPage({
  saveId,
  urlSeason,
  onQualifiersChange,
}: {
  saveId: string;
  urlSeason: number | null;
  onQualifiersChange: (selection: QualifiersSelection) => void;
}) {
  const [season, setSeason] = useState<number | null>(urlSeason);
  const [running, setRunning] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);
  const [actionDone, setActionDone] = useState<string | null>(null);

  useEffect(() => {
    setSeason(urlSeason);
  }, [urlSeason, saveId]);

  const detailState = useAsync<SaveDetail>(saveId, (signal) => fetchSaveDetail(saveId, signal));
  const statusState = useAsync<SeasonStatus>(saveId ? `${saveId}/status` : null, (signal) =>
    fetchSeasonStatus(saveId, signal),
  );
  const seasonsState = useAsync<HistorySeasons>(saveId, (signal) => fetchHistorySeasons(saveId, signal));

  const currentSeason = detailState.data?.currentSeason ?? null;
  const status = statusState.data;
  const effectiveSeason = season ?? status?.sourceSeasonNumber ?? null;

  const progressKey =
    saveId && currentSeason !== null ? `${saveId}/progress-s${currentSeason}` : null;
  const progressState = useAsync<SeasonProgress>(progressKey, (signal) =>
    fetchSeasonProgress(saveId, currentSeason as number, signal),
  );

  const listKey =
    saveId && effectiveSeason !== null
      ? `${saveId}/qualifiers-s${effectiveSeason}`
      : saveId
        ? `${saveId}/qualifiers-latest`
        : null;
  const listState = useAsync<QualifierList>(listKey, (signal) =>
    fetchQualifierList(saveId, effectiveSeason, signal),
  );

  const seasons = seasonsState.data?.seasons ?? [];
  const includeFeeders = useMemo(() => {
    if (listState.data && listState.data.events.some((event) => event.boundary !== 'Superleague')) {
      return true;
    }
    return (progressState.data?.leagues ?? []).some(
      (league) => league.leagueLevel === 'Feeder2' || league.leagueLevel === 'Feeder3' || league.feederDivision === 2 || league.feederDivision === 3,
    );
  }, [listState.data, progressState.data]);

  const overview = useMemo(
    () => buildQualifierOverview(listState.data, includeFeeders),
    [listState.data, includeFeeders],
  );
  const completed = overview.filter((entry) => entry.status === 'complete').length;
  const canRun = (status?.legalNextActions ?? []).includes('RunQualifier');

  async function refreshAll(): Promise<void> {
    listState.refresh();
    statusState.refresh();
  }

  async function handleRunRemaining(): Promise<void> {
    if (running) {
      return;
    }
    setRunning(true);
    setActionError(null);
    setActionDone(null);
    try {
      const result = await runRemainingQualifiers(saveId);
      setActionDone(
        `Ran ${result.executedNow.length} qualifier${result.executedNow.length === 1 ? '' : 's'} in canonical order (${result.executedNow.join(', ') || 'none pending'}).`,
      );
      await refreshAll();
    } catch (failure) {
      setActionError(apiErrorMessage(failure));
    } finally {
      setRunning(false);
    }
  }

  if ((detailState.loading && !detailState.data) || (seasonsState.loading && seasons.length === 0)) {
    return <Loading label="Loading qualifiers…" />;
  }

  return (
    <div className="dashboard">
      <div className="toolbar" role="group" aria-label="Qualifier filters">
        <label className="field">
          <span>Season</span>
          <select
            value={effectiveSeason ?? ''}
            disabled={seasons.length === 0}
            onChange={(changed) => {
              const next = Number.parseInt(changed.target.value, 10);
              const selectedSeason = Number.isNaN(next) ? null : next;
              setSeason(selectedSeason);
              onQualifiersChange({ season: selectedSeason });
            }}
          >
            {effectiveSeason === null ? <option value="">Latest resolved</option> : null}
            {seasons.map((row) => (
              <option key={row.seasonNumber} value={row.seasonNumber}>
                Season {row.seasonNumber}
                {row.isComplete ? '' : ' (in progress)'}
              </option>
            ))}
          </select>
        </label>
        <div className="toolbar-end">
          <Link to={dashboardPath(saveId)} className="ghost-button">
            Dashboard
          </Link>
        </div>
      </div>

      <Card
        eyebrow="Postseason qualifiers"
        title={
          effectiveSeason !== null
            ? `Season ${effectiveSeason} qualifiers — ${completed}/${overview.length} complete`
            : `Latest qualifiers — ${completed}/${overview.length} complete`
        }
        action={
          canRun ? (
            <button
              type="button"
              className="primary-button"
              disabled={running}
              aria-busy={running}
              onClick={() => {
                void handleRunRemaining();
              }}
            >
              {running ? 'Running…' : 'Run remaining qualifiers'}
            </button>
          ) : undefined
        }
        info={
          <p>
            Tiered saves resolve 17 qualifiers in canonical order — Superleague first, then
            Feeder 1↔Feeder 2 by color, then Feeder 2↔Feeder 3 by color. Single-feeder (v1)
            saves resolve one 32-athlete Superleague qualifier. Every entry below reads
            persisted results only and never resimulates.
          </p>
        }
      >
        {listState.loading && !listState.data ? (
          <Loading label="Loading qualifier results…" />
        ) : listState.error ? (
          <Notice tone="error" title="Qualifiers unavailable">
            <p>{listState.error}</p>
          </Notice>
        ) : (
          <>
            {actionError ? (
              <Notice tone="error" title="The qualifier run did not complete">
                <p>{actionError}</p>
                <p>
                  <button type="button" className="ghost-button" onClick={() => setActionError(null)}>
                    Dismiss
                  </button>
                </p>
              </Notice>
            ) : null}
            {actionDone ? (
              <p className="flow-done" role="status">
                <span aria-hidden="true">✓ </span>
                {actionDone}
              </p>
            ) : null}
            {!listState.data ? (
              <Notice tone="empty" title="No qualifiers resolved yet">
                <p>
                  {canRun
                    ? 'The qualifier phase is next. Run remaining qualifiers above, or open a pending qualifier below to play it individually.'
                    : 'Qualifiers resolve after automatic promotion/relegation. Return once the postseason reaches them.'}
                </p>
              </Notice>
            ) : null}
            <QualifierGroups
              saveId={saveId}
              overview={overview}
              season={effectiveSeason ?? listState.data?.fromSeasonNumber ?? null}
              canRun={canRun}
            />
          </>
        )}
      </Card>
    </div>
  );
}

function QualifierGroups({
  saveId,
  overview,
  season,
  canRun,
}: {
  saveId: string;
  overview: QualifierEntry[];
  season: number | null;
  canRun: boolean;
}) {
  const groups: Array<{ label: string; entries: QualifierEntry[] }> = [];
  const sl = overview.filter((entry) => entry.boundary === 'Superleague');
  if (sl.length > 0) {
    groups.push({ label: 'Superleague', entries: sl });
  }
  const f1f2 = overview.filter((entry) => entry.boundary === 'Feeder1Feeder2');
  if (f1f2.length > 0) {
    groups.push({ label: 'Feeder 1 ↔ Feeder 2', entries: f1f2 });
  }
  const f2f3 = overview.filter((entry) => entry.boundary === 'Feeder2Feeder3');
  if (f2f3.length > 0) {
    groups.push({ label: 'Feeder 2 ↔ Feeder 3', entries: f2f3 });
  }
  const other = overview.filter(
    (entry) => entry.boundary !== 'Superleague' && entry.boundary !== 'Feeder1Feeder2' && entry.boundary !== 'Feeder2Feeder3',
  );
  if (other.length > 0) {
    groups.push({ label: 'Other', entries: other });
  }

  return (
    <>
      {groups.map((group) => (
        <section key={group.label} aria-label={`${group.label} qualifiers`}>
          <h3 className="reveal-subhead">
            {group.label} — {group.entries.filter((entry) => entry.status === 'complete').length}/{group.entries.length} complete
          </h3>
          <div className="table-wrap">
            <table className="data-table">
              <thead>
                <tr>
                  <th scope="col">Qualifier</th>
                  <th scope="col">Field</th>
                  <th scope="col">Status</th>
                </tr>
              </thead>
              <tbody>
                {group.entries.map((entry) => (
                  <tr key={entry.key}>
                    <td>
                      <Link
                        to={qualifierPath(saveId, boundaryOf(entry), entry.sportingColorName === '-' ? null : entry.sportingColorName, { season })}
                      >
                        {entry.title}
                      </Link>
                    </td>
                    <td>
                      {entry.qualifierSize !== null
                        ? `${entry.qualifierSize} athletes · ${entry.roundCount ?? '—'} rounds · top ${entry.winners ?? 8} qualify`
                        : entry.boundary === 'Superleague'
                          ? '32 athletes · 16 rounds · top 8 qualify'
                          : '16 athletes · 16 rounds · top 8 qualify'}
                    </td>
                    <td>
                      {entry.status === 'complete' ? (
                        <span className="badge badge-done">Complete</span>
                      ) : canRun ? (
                        <span className="badge badge-ready">Pending</span>
                      ) : (
                        <span className="badge badge-wait">Pending</span>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      ))}
    </>
  );
}

function boundaryOf(entry: QualifierEntry): 'Superleague' | 'Feeder1Feeder2' | 'Feeder2Feeder3' {
  if (entry.boundary === 'Feeder1Feeder2') {
    return 'Feeder1Feeder2';
  }
  if (entry.boundary === 'Feeder2Feeder3') {
    return 'Feeder2Feeder3';
  }
  return 'Superleague';
}

/**
 * One qualifier event (MSS-060): the same presentation serves the
 * 32-athlete Superleague field and 16-athlete feeder fields. Field size,
 * incumbent/challenger roles, source ranks, rounds and the top-8 cutoff all
 * come from persisted data. Pending qualifiers can be run individually;
 * resolved ones replay persisted standings without resimulation.
 */
export function QualifierDetailPage({
  saveId,
  boundary,
  color,
  urlSeason,
}: {
  saveId: string;
  boundary: 'Superleague' | 'Feeder1Feeder2' | 'Feeder2Feeder3';
  color: string | null;
  urlSeason: number | null;
}) {
  const [running, setRunning] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);

  const statusState = useAsync<SeasonStatus>(saveId ? `${saveId}/status` : null, (signal) =>
    fetchSeasonStatus(saveId, signal),
  );
  const status = statusState.data;
  const effectiveSeason = urlSeason ?? status?.sourceSeasonNumber ?? null;
  const listKey =
    saveId && effectiveSeason !== null
      ? `${saveId}/qualifiers-s${effectiveSeason}`
      : saveId
        ? `${saveId}/qualifiers-latest`
        : null;
  const listState = useAsync<QualifierList>(listKey, (signal) =>
    fetchQualifierList(saveId, effectiveSeason, signal),
  );

  const event: QualifierEvent | null = useMemo(() => {
    const events = listState.data?.events ?? [];
    return (
      events.find(
        (candidate) =>
          candidate.boundary === boundary &&
          (boundary === 'Superleague' ||
            candidate.sportingColorName.toLowerCase() === (color ?? '').toLowerCase()),
      ) ?? null
    );
  }, [listState.data, boundary, color]);

  const canRun = (status?.legalNextActions ?? []).includes('RunQualifier');
  const rows = useMemo(() => (event ? qualifierFieldRows(event) : []), [event]);
  const pending = !listState.loading && listState.error === null && listState.data === null;

  async function handleRunSingle(): Promise<void> {
    if (running) {
      return;
    }
    setRunning(true);
    setError(null);
    setDone(null);
    try {
      if (boundary === 'Superleague') {
        await runEventRemaining(saveId, 'qualifier');
        setDone('Superleague qualifier finished — all 16 rounds persisted.');
      } else {
        const slug = qualifierBoundarySegment(boundary);
        await runFeederQualifier(saveId, slug === 'f1f2' ? 'F1F2' : 'F2F3', color ?? '');
        setDone(`${eventTitle()} finished — 16 rounds persisted in canonical order.`);
      }
      listState.refresh();
      statusState.refresh();
    } catch (failure) {
      setError(apiErrorMessage(failure));
    } finally {
      setRunning(false);
    }
  }

  async function handleRunRemaining(): Promise<void> {
    if (running) {
      return;
    }
    setRunning(true);
    setError(null);
    setDone(null);
    try {
      const result = await runRemainingQualifiers(saveId);
      setDone(`Ran ${result.executedNow.length} qualifier${result.executedNow.length === 1 ? '' : 's'} in canonical order.`);
      listState.refresh();
      statusState.refresh();
    } catch (failure) {
      setError(apiErrorMessage(failure));
    } finally {
      setRunning(false);
    }
  }

  function eventTitle(): string {
    if (boundary === 'Superleague') {
      return 'Superleague qualifier';
    }
    return `${boundary === 'Feeder1Feeder2' ? 'Feeder 1 ↔ Feeder 2' : 'Feeder 2 ↔ Feeder 3'} · ${color ?? ''}`;
  }

  if (listState.loading && !listState.data) {
    return <Loading label="Loading qualifier…" />;
  }

  return (
    <div className="dashboard">
      <div className="toolbar" role="group" aria-label="Qualifier navigation">
        <div className="toolbar-end">
          <Link to={qualifiersPath(saveId, { season: effectiveSeason })} className="ghost-button">
            All qualifiers
          </Link>
          <Link to={dashboardPath(saveId)} className="ghost-button">
            Dashboard
          </Link>
          {boundary === 'Superleague' && event ? (
            <Link to={livePath(saveId, { event: 'qualifier', season: event.fromSeasonNumber })} className="ghost-button">
              Play round-by-round on Live
            </Link>
          ) : null}
        </div>
      </div>

      <Card
        eyebrow="Qualifier"
        title={effectiveSeason !== null ? `${eventTitle()} · Season ${effectiveSeason}` : eventTitle()}
        action={
          canRun ? (
            <div className="live-buttons">
              <button
                type="button"
                className="primary-button"
                disabled={running}
                aria-busy={running}
                onClick={() => {
                  void handleRunSingle();
                }}
              >
                {running ? 'Running…' : event ? 'Re-run is blocked — already resolved' : `Run this qualifier`}
              </button>
              <button
                type="button"
                className="ghost-button"
                disabled={running || event !== null}
                title="Runs every remaining qualifier in canonical order."
                onClick={() => {
                  void handleRunRemaining();
                }}
              >
                Run remaining qualifiers
              </button>
            </div>
          ) : undefined
        }
        info={
          <p>
            {boundary === 'Superleague'
              ? 'Superleague places 17–24 defend against Feeder 1 runners-up (places 2–4): 32 athletes, 16 rounds, top 8 take the last Superleague places.'
              : boundary === 'Feeder1Feeder2'
                ? 'Feeder 1 places 17–24 defend against Feeder 2 places 9–16: 16 athletes, 16 rounds, top 8 start next season in Feeder 1.'
                : 'Feeder 2 places 17–24 defend against Feeder 3 places 9–16: 16 athletes, 16 rounds, top 8 start next season in Feeder 2.'}{' '}
            Qualifiers run in canonical order; an out-of-order run is refused by the backend, never silently reordered.
          </p>
        }
      >
        {listState.error ? (
          <Notice tone="error" title="Qualifier unavailable">
            <p>{listState.error}</p>
          </Notice>
        ) : error ? (
          <Notice tone="error" title="The qualifier run did not complete">
            <p>{error}</p>
            <p>
              <button type="button" className="ghost-button" onClick={() => setError(null)}>
                Dismiss
              </button>
            </p>
          </Notice>
        ) : null}
        {done ? (
          <p className="flow-done" role="status">
            <span aria-hidden="true">✓ </span>
            {done}
          </p>
        ) : null}
        {!event ? (
          <Notice tone="empty" title={pending ? 'Qualifier pending' : 'Qualifier not found'}>
            <p>
              {pending
                ? 'This qualifier has not been resolved yet. Run it individually above, run all remaining qualifiers, or continue from the Dashboard — already-resolved qualifiers are never rerun.'
                : 'No persisted result matches this qualifier identity for the selected season.'}
            </p>
          </Notice>
        ) : (
          <>
            <p className="muted small">{qualifierProgressLine({ key: '', boundary, sportingColor: event.sportingColor, sportingColorName: event.sportingColorName, title: '', order: 0, status: 'complete', qualifierSize: event.qualifierSize, roundCount: event.roundCount, winners: event.winners, qualifiedCount: rows.filter((row) => row.isQualified).length, event })}</p>
            <div className="table-wrap">
              <table className="data-table">
                <thead>
                  <tr>
                    <th scope="col" className="numeric">Qualifier rank</th>
                    <th scope="col">Card</th>
                    <th scope="col">Role</th>
                    <th scope="col" className="numeric">From rank</th>
                    <th scope="col" className="numeric">Score</th>
                    <th scope="col" className="numeric">Round W</th>
                    <th scope="col">Outcome</th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map((row) => (
                    <tr key={row.athleteId}>
                      <td className="numeric">{row.qualifierRank}</td>
                      <td>
                        <AthleteLink saveId={saveId} athleteId={row.athleteId} name={row.name} />
                        <span className="card-sub"> · {row.sportingColor}</span>
                      </td>
                      <td>
                        {qualifierRoleLabel(row.role)}
                        <span className="card-sub"> · {row.fromLeagueName}</span>
                      </td>
                      <td className="numeric">P{row.fromSeasonRank}</td>
                      <td className="numeric">{formatPoints(row.qualifierScoreThousandths)}</td>
                      <td className="numeric">{row.roundWins}</td>
                      <td>
                        <span className={row.isQualified ? 'badge badge-done' : 'badge badge-wait'}>
                          {qualifierOutcomeLabel(row)}
                        </span>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <p className="muted small">
              Persisted final standings; refresh replays the same rows and never resimulates. Checksum{' '}
              <code title={event.checksum}>{event.checksum.slice(0, 12)}…</code>
            </p>
          </>
        )}
      </Card>
    </div>
  );
}
