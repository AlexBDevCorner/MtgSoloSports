import { useEffect, useMemo, useState } from 'react';
import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import { ApiError, apiErrorMessage } from '../../shared/api/http';
import { AthleteLink } from '../routing/router';
import { Link } from '../routing/router';
import { cupEditionPath, recordsPath, type CupKind, type RecordsView } from '../routing/routes';
import { LeagueLink } from '../leagues/LeagueLink';
import { TeamBadge } from '../cups/TeamBadge';
import { ColorTeamLink } from '../cups/ColorTeamLink';
import { cupTeamKey } from '../cups/cupTeamKeys';
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
import {
  buildLeagueGrid,
  CAREER_HOLDER_PREVIEW_COUNT,
  cupEventDisplayName,
  formatScoringContext,
  groupCupRecords,
  leagueMetricDisplayName,
  leagueScopeDisplayName,
  splitHolderPreview,
} from './scoringFormat';
import {
  careerRecordLabel,
  honourKindLabel,
  scoringRecordLabel,
} from '../../shared/displayLabels';
import './RecordsPage.css';

type PeriodMode = 'all' | 'single' | 'range';

interface PeriodSelection {
  mode: PeriodMode;
  season: number | null;
  from: number | null;
  to: number | null;
}

const DEFAULT_PERIOD: PeriodSelection = { mode: 'all', season: null, from: null, to: null };

/** MSS-140 view order and switcher labels. `fame` is the default view. */
const VIEW_LABELS: Record<RecordsView, string> = {
  fame: 'Hall of Fame',
  career: 'Career records',
  scoring: 'Scoring records',
  honours: 'Honours',
};

const VIEW_ORDER: readonly RecordsView[] = ['fame', 'career', 'scoring', 'honours'];

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

/** Non-empty trimmed text, or null when there is no reliable identity to link. */
function nonEmpty(value: string | null | undefined): string | null {
  if (typeof value !== 'string') {
    return null;
  }
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : null;
}

/** Route-ready Cup for Cup honours ("color" | "type" | "chaos"); null renders plain text. */
function honourCupKind(honourKind: string | null | undefined): CupKind | null {
  if (typeof honourKind !== 'string') {
    return null;
  }
  if (honourKind.startsWith('ColorCup')) {
    return 'color';
  }
  if (honourKind.startsWith('TypeCup')) {
    return 'type';
  }
  if (honourKind.startsWith('ChaosCup')) {
    return 'chaos';
  }
  return null;
}

/** Route-ready Cup for a team scoring record, derived from the record key; null for non-team records. */
function scoringTeamCup(recordKey: string | null | undefined): 'color' | 'type' | null {
  if (typeof recordKey !== 'string') {
    return null;
  }
  if (recordKey.startsWith('colour_cup_team')) {
    return 'color';
  }
  if (recordKey.startsWith('type_cup_team')) {
    return 'type';
  }
  return null;
}

/** Team page link for a scoring record holder, or plain text when identity is missing. */
function ScoringTeamLink({
  saveId,
  recordKey,
  teamName,
  teamKey,
}: {
  saveId: string;
  recordKey: string;
  teamName: string | null;
  teamKey: string | null;
}) {
  const cup = scoringTeamCup(recordKey);
  const key = cup ? cupTeamKey(cup, teamName || teamKey) : null;
  const display = teamName || teamKey || '—';
  if (!cup || !key) {
    return <span>{display}</span>;
  }
  return <TeamBadge saveId={saveId} cup={cup} teamKey={key} teamName={display} />;
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
  recordKey,
  holders,
}: {
  saveId: string;
  recordKey: string;
  holders: ScoringRecordHolder[];
}) {
  return (
    <>
      {holders.map((holder, index) => (
        <div key={`${holder.athleteId ?? holder.teamKey}-${holder.seasonNumber}-${holder.roundNumber ?? '-'}-${holder.stageNumber ?? '-'}-${holder.groupNumber ?? '-'}-${index}`}>
          {holder.athleteId !== null && holder.athleteName ? (
            <AthleteLink saveId={saveId} athleteId={holder.athleteId} name={holder.athleteName} />
          ) : (
            <ScoringTeamLink
              saveId={saveId}
              recordKey={recordKey}
              teamName={holder.teamName}
              teamKey={holder.teamKey}
            />
          )}
          {holder.athleteId !== null && holder.teamKey ? (
            <span className="card-sub">
              {' '}
              (
              <ScoringTeamLink
                saveId={saveId}
                recordKey={recordKey}
                teamName={holder.teamName}
                teamKey={holder.teamKey}
              />
              )
            </span>
          ) : undefined}
          <span className="card-sub"> · {formatScoringContext(holder)}</span>
          {index < holders.length - 1 ? <span>, </span> : undefined}
        </div>
      ))}
    </>
  );
}

/**
 * MSS-140 career holder list: the first holders render inline with the
 * total count next to the value; longer ties collapse behind an
 * accessible "+N more" toggle that expands the full list in place.
 */
function CareerHolders({
  saveId,
  label,
  holders,
}: {
  saveId: string;
  label: string;
  holders: { athleteId: number; athleteName: string }[];
}) {
  const [expanded, setExpanded] = useState(false);
  const { visible, hiddenCount } = splitHolderPreview(holders, CAREER_HOLDER_PREVIEW_COUNT);
  const shown = expanded ? holders : visible;
  if (holders.length === 0) {
    return <span className="muted">Vacant</span>;
  }
  return (
    <span>
      {shown.map((holder, index) => (
        <span key={holder.athleteId}>
          {index > 0 ? ', ' : ''}
          <AthleteLink saveId={saveId} athleteId={holder.athleteId} name={holder.athleteName} />
        </span>
      ))}
      {hiddenCount > 0 ? (
        <button
          type="button"
          className="ghost-button records-more"
          aria-expanded={expanded}
          aria-label={
            expanded
              ? `Show fewer holders for ${label}`
              : `Show all ${holders.length} holders for ${label}`
          }
          onClick={() => {
            setExpanded((current) => !current);
          }}
        >
          {expanded ? 'Show fewer' : `+${hiddenCount} more`}
        </button>
      ) : undefined}
    </span>
  );
}

/** One League grid cell: value plus holder(s) and season/stage/round context. */
function LeagueGridCell({ saveId, record }: { saveId: string; record: ScoringRecord | undefined }) {
  if (!record) {
    return <span className="muted">Vacant</span>;
  }
  if (record.isVacant) {
    return <span className="muted">Vacant</span>;
  }
  return (
    <span>
      <strong>{record.valueDisplay}</strong>
      <br />
      <ScoringHolders saveId={saveId} recordKey={record.recordKey} holders={record.holders} />
    </span>
  );
}

/** Cup event group table: record labels stay short while holder context wraps fully. */
function CupRecordsTable({ saveId, records }: { saveId: string; records: ScoringRecord[] }) {
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
                <span className="records-label">{scoringRecordLabel(record.recordKey, record.label)}</span>
              </td>
              <td className="numeric">{record.valueDisplay}</td>
              <td>
                {record.isVacant ? (
                  <span className="muted">Vacant</span>
                ) : (
                  <ScoringHolders saveId={saveId} recordKey={record.recordKey} holders={record.holders} />
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

function HallOfFameView({
  saveId,
  fame,
  fameLoading,
  fameError,
  period,
  reconciled,
  seasons,
  periodInvalid,
  onPeriodChange,
}: {
  saveId: string;
  fame: HallOfFame | null;
  fameLoading: boolean;
  fameError: string | null;
  period: PeriodSelection;
  reconciled: PeriodSelection;
  seasons: number[];
  periodInvalid: boolean;
  onPeriodChange: (next: PeriodSelection) => void;
}) {
  if (fameLoading && !fame) {
    return <Loading label="Loading records…" />;
  }
  if (fameError && !fame) {
    return (
      <Notice tone="error" title="Records unavailable">
        <p>{fameError}</p>
      </Notice>
    );
  }
  const leaders = fame?.leaders ?? [];
  return (
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
                onPeriodChange(DEFAULT_PERIOD);
              } else if (mode === 'single') {
                const fallback = seasons[seasons.length - 1] ?? 1;
                onPeriodChange({ mode: 'single', season: reconciled.season ?? fallback, from: null, to: null });
              } else {
                const fallback = seasons[seasons.length - 1] ?? 1;
                onPeriodChange({
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
                onPeriodChange({ mode: 'single', season: Number(event.target.value), from: null, to: null });
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
                  onPeriodChange({ mode: 'range', season: null, from: Number(event.target.value), to: reconciled.to });
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
                  onPeriodChange({ mode: 'range', season: null, from: reconciled.from, to: Number(event.target.value) });
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
                      · <ColorTeamLink saveId={saveId} sportingColor={row.sportingColor} sportingColorName={row.sportingColorName} />
                      {row.superleagueTitles > 0
                        ? ` · ${row.superleagueTitles} SL`
                        : ''}
                    </span>
                  </td>
                  <td className="numeric">{row.careerPoints}</td>
                  <td className="numeric">
                    <span className="records-nowrap">
                      {row.totalTitles} ({row.feederTitles}F · {row.superleagueTitles}S)
                    </span>
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
  );
}

function CareerRecordsView({
  saveId,
  records,
  loading,
  error,
}: {
  saveId: string;
  records: Records | null;
  loading: boolean;
  error: string | null;
}) {
  if (loading && !records) {
    return <Loading label="Loading records…" />;
  }
  if (error && !records) {
    return (
      <Notice tone="error" title="Records unavailable">
        <p>{error}</p>
      </Notice>
    );
  }
  const career = records?.records ?? [];
  return (
    <Card
      eyebrow="Records"
      title={`Career records — ${career.length}`}
      info={
        <p>
          Ties share the record; only an outright higher value replaces holders and emits a
          new-record story.
        </p>
      }
    >
      {career.length === 0 ? (
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
              {career.map((record) => (
                <tr key={record.recordKey}>
                  <td>{careerRecordLabel(record.recordKey, record.label)}</td>
                  <td className="numeric">
                    <span className="records-nowrap">{record.valueDisplay}</span>
                    {record.isVacant ? undefined : (
                      <span className="card-sub">
                        {' '}
                        · {record.holders.length} {record.holders.length === 1 ? 'holder' : 'holders'}
                      </span>
                    )}
                  </td>
                  <td>
                    <CareerHolders
                      saveId={saveId}
                      label={careerRecordLabel(record.recordKey, record.label)}
                      holders={record.holders}
                    />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Card>
  );
}

function ScoringRecordsView({
  saveId,
  records,
  loading,
  error,
}: {
  saveId: string;
  records: Records | null;
  loading: boolean;
  error: string | null;
}) {
  const scoring = useMemo(() => records?.scoringRecords ?? [], [records]);
  const grid = useMemo(() => buildLeagueGrid(scoring), [scoring]);
  const individualGroups = useMemo(() => groupCupRecords(scoring, 'Individual Cups'), [scoring]);
  const teamGroups = useMemo(() => groupCupRecords(scoring, 'Team Cups'), [scoring]);

  if (loading && !records) {
    return <Loading label="Loading records…" />;
  }
  if (error && !records) {
    return (
      <Notice tone="error" title="Records unavailable">
        <p>{error}</p>
      </Notice>
    );
  }
  return (
    <>
      <Card
        eyebrow="Scoring records"
        title="League scoring"
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
        {scoring.length === 0 ? (
          <p className="muted">No records yet.</p>
        ) : (
          <div className="table-wrap">
            <table className="data-table records-league-grid">
              <thead>
                {/* Column order mirrors LEAGUE_METRIC_ORDER (round, stage, season);
                    headers stay explicit so the MSS-046 alignment audit sees
                    one <th> per column. */}
                <tr>
                  <th scope="col">League</th>
                  <th scope="col">{leagueMetricDisplayName('round')}</th>
                  <th scope="col">{leagueMetricDisplayName('stage')}</th>
                  <th scope="col">{leagueMetricDisplayName('season')}</th>
                </tr>
              </thead>
              <tbody>
                {grid.map((row) => (
                  <tr key={row.scopeKey}>
                    <th scope="row">{leagueScopeDisplayName(row.scopeKey)}</th>
                    <td>
                      <LeagueGridCell saveId={saveId} record={row.round} />
                    </td>
                    <td>
                      <LeagueGridCell saveId={saveId} record={row.stage} />
                    </td>
                    <td>
                      <LeagueGridCell saveId={saveId} record={row.season} />
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
        title="Individual Cups"
        info={
            <p>
              Color Cup individual and Qualifier scoring, separated by event type and
              round/stage scope. Values come from persisted Cup and Qualifier results so
              old seasons keep contributing after new seasons start.
            </p>
        }
      >
        {scoring.length === 0 ? (
          <p className="muted">No records yet.</p>
        ) : individualGroups.length === 0 ? (
          <p className="muted">No records yet.</p>
        ) : (
          individualGroups.map((group) => (
            <section key={group.eventKey} aria-label={cupEventDisplayName(group.eventKey)}>
              <h3 className="section-title">{cupEventDisplayName(group.eventKey)}</h3>
              <CupRecordsTable saveId={saveId} records={group.records} />
            </section>
          ))
        )}
      </Card>

      <Card
        eyebrow="Scoring records"
        title="Team Cups"
        info={
            <p>
              Color Cup and Type Cup team scoring, separated by event type and
              leg/group-stage scope. Individual leg rounds, leg group-stage totals, team
              single-round totals and full team-event totals are tracked separately.
            </p>
        }
      >
        {scoring.length === 0 ? (
          <p className="muted">No records yet.</p>
        ) : teamGroups.length === 0 ? (
          <p className="muted">No records yet.</p>
        ) : (
          teamGroups.map((group) => (
            <section key={group.eventKey} aria-label={cupEventDisplayName(group.eventKey)}>
              <h3 className="section-title">{cupEventDisplayName(group.eventKey)}</h3>
              <CupRecordsTable saveId={saveId} records={group.records} />
            </section>
          ))
        )}
      </Card>
    </>
  );
}

function HonoursView({
  saveId,
  honours,
  honoursLoading,
  honoursError,
  history,
  historyLoading,
  historyError,
}: {
  saveId: string;
  honours: Honours | null;
  honoursLoading: boolean;
  honoursError: string | null;
  history: Records | null;
  historyLoading: boolean;
  historyError: string | null;
}) {
  const rows = honours?.honours ?? [];
  const breaks = history?.recentHistory ?? [];
  return (
    <>
      <Card eyebrow="History" title={`Record breaks — ${breaks.length} recent`}>
        {historyLoading && !history ? (
          <Loading label="Loading records…" />
        ) : historyError && !history ? (
          <Notice tone="error" title="Records unavailable">
            <p>{historyError}</p>
          </Notice>
        ) : breaks.length === 0 ? (
          <p className="muted">
            No record breaks yet. New outright records appear here after season
            finalization.
          </p>
        ) : (
          <ul className="story-list">
            {breaks.map((item, index) => (
              <li key={`${item.recordKey}-${item.athleteId}-${item.seasonNumber}-${index}`}>
                <span className="badge badge-ready">{careerRecordLabel(item.recordKey)}</span>{' '}
                <AthleteLink saveId={saveId} athleteId={item.athleteId} name={item.athleteName} />{' '}
                <span>{item.text}</span>
              </li>
            ))}
          </ul>
        )}
      </Card>

      <Card eyebrow="Honours" title={`Official honours — ${rows.length}`}>
        {honoursLoading && rows.length === 0 ? (
          <Loading label="Loading honours…" />
        ) : honoursError && rows.length === 0 ? (
          <Notice tone="error" title="Records unavailable">
            <p>{honoursError}</p>
          </Notice>
        ) : rows.length === 0 ? (
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
                {rows.map((row) => (
                  <tr key={`${row.seasonNumber}-${row.leagueId}-${row.athleteId}-${row.honourKind}`}>
                    <td className="numeric">{row.seasonNumber}</td>
                    <td>
                      {nonEmpty(row.leagueKey) ? (
                        <LeagueLink
                          saveId={saveId}
                          leagueKey={row.leagueKey as string}
                          title={`Open the ${row.leagueName} league profile`}
                        >
                          {row.leagueName}
                        </LeagueLink>
                      ) : honourCupKind(row.honourKind) ? (
                        <Link
                          to={cupEditionPath(saveId, honourCupKind(row.honourKind) as CupKind, row.seasonNumber)}
                          title={`Open the ${row.leagueName} edition for Season ${row.seasonNumber}`}
                        >
                          {row.leagueName}
                        </Link>
                      ) : (
                        row.leagueName
                      )}
                    </td>
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
    </>
  );
}

export function RecordsPage({ saveId, urlView }: { saveId: string; urlView: RecordsView | null }) {
  // MSS-140: the URL view is authoritative. Unknown or absent views fall
  // back to Hall of Fame; refresh, Back/Forward and copied URLs restore the
  // same view. Only the current view's endpoints load.
  const view: RecordsView = urlView ?? 'fame';
  const needRecords = view !== 'fame';
  const needHonours = view === 'honours';
  const needFame = view === 'fame';

  const recordsState = useSaveData<Records>(needRecords ? saveId : null, (id, signal) =>
    fetchRecords(id, signal),
  );
  const honoursState = useSaveData<Honours>(needHonours ? saveId : null, (id, signal) =>
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
    if (!needFame) {
      return;
    }
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
  }, [saveId, needFame]);

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
    if (!needFame || periodInvalid) {
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
  }, [saveId, needFame, query.fromSeason, query.toSeason]);

  return (
    <div className="records-page">
      <div className="toolbar records-toolbar">
        <div className="field">
          <span id="records-view-label">View</span>
          <div className="segmented records-switcher" role="group" aria-labelledby="records-view-label">
            {VIEW_ORDER.map((key) =>
              key === view ? (
                <span key={key} className="nav-item current" aria-current="page">
                  {VIEW_LABELS[key]}
                </span>
              ) : (
                <Link
                  key={key}
                  to={recordsPath(saveId, { view: key })}
                  className="nav-item"
                  title={`Show ${VIEW_LABELS[key]}`}
                >
                  {VIEW_LABELS[key]}
                </Link>
              ),
            )}
          </div>
        </div>
      </div>

      {view === 'fame' ? (
        <HallOfFameView
          saveId={saveId}
          fame={fame}
          fameLoading={fameLoading}
          fameError={fameError}
          period={period}
          reconciled={reconciled}
          seasons={seasons}
          periodInvalid={periodInvalid}
          onPeriodChange={setPeriod}
        />
      ) : undefined}
      {view === 'career' ? (
        <CareerRecordsView
          saveId={saveId}
          records={recordsState.data}
          loading={recordsState.loading}
          error={recordsState.error}
        />
      ) : undefined}
      {view === 'scoring' ? (
        <ScoringRecordsView
          saveId={saveId}
          records={recordsState.data}
          loading={recordsState.loading}
          error={recordsState.error}
        />
      ) : undefined}
      {view === 'honours' ? (
        <HonoursView
          saveId={saveId}
          honours={honoursState.data}
          honoursLoading={honoursState.loading}
          honoursError={honoursState.error}
          history={recordsState.data}
          historyLoading={recordsState.loading}
          historyError={recordsState.error}
        />
      ) : undefined}
    </div>
  );
}
