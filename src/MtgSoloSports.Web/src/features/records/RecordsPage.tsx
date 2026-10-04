import { useEffect, useState } from 'react';
import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import { ApiError, apiErrorMessage } from '../../shared/api/http';
import { AthleteLink } from '../routing/router';
import {
  fetchHallOfFame,
  fetchHonours,
  fetchRecords,
  type HallOfFame,
  type Honours,
  type Records,
  type ScoringRecord,
  type ScoringRecordHolder,
} from './recordsApi';
import { formatScoringContext } from './scoringFormat';

/** Display-only projection of a fixed-point bonus (no sporting math). */
function formatBonus(thousandths: number): string {
  const sign = thousandths >= 0 ? '+' : '';
  return `${sign}${(thousandths / 1000).toFixed(3)}%`;
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
                {record.label}
                <span className="card-sub"> · {record.scope}</span>
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
  const fameState = useSaveData<HallOfFame>(saveId, (id, signal) =>
    fetchHallOfFame(id, 20, signal),
  );
  const honoursState = useSaveData<Honours>(saveId, (id, signal) =>
    fetchHonours(id, signal),
  );

  const loading =
    (recordsState.loading && !recordsState.data) ||
    (fameState.loading && !fameState.data);
  if (loading) {
    return <Loading label="Loading records…" />;
  }

  const error = recordsState.error ?? fameState.error ?? honoursState.error;
  if (error && !recordsState.data && !fameState.data) {
    return (
      <Notice tone="error" title="Records unavailable">
        <p>{error}</p>
      </Notice>
    );
  }

  const records = recordsState.data?.records ?? [];
  const history = recordsState.data?.recentHistory ?? [];
  const scoring = recordsState.data?.scoringRecords ?? [];
  const leaders = fameState.data?.leaders ?? [];
  const honours = honoursState.data?.honours ?? [];
  const leagueScoring = scoring.filter((r) => r.category === 'League');
  const individualScoring = scoring.filter((r) => r.category === 'Individual Cups');
  const teamScoring = scoring.filter((r) => r.category === 'Team Cups');

  return (
    <div className="page-grid">
      <Card
        eyebrow="Hall of Fame"
        title={`Career leaders — ${leaders.length} shown`}
        action={
          fameState.loading ? (
            <span className="muted small">Refreshing…</span>
          ) : undefined
        }
        info={
          <p>
            Ordered by total titles, Superleague titles, stage wins, round wins, then name.
            Computed from normalized titles and career projections without decompressing round
            payloads.
          </p>
        }
      >
        {leaders.length === 0 ? (
          <p className="muted">
            No leaders yet. Complete a season to persist official honours.
          </p>
        ) : (
          <div className="table-wrap">
            <table className="data-table">
              <thead>
                <tr>
                    <th scope="col" className="numeric">Rank</th>
                    <th scope="col">Card</th>
                    <th scope="col" className="numeric">Titles</th>
                    <th scope="col" className="numeric">Stage W</th>
                    <th scope="col" className="numeric">Round W</th>
                    <th scope="col" className="numeric">Tenure</th>
                    <th scope="col" className="numeric">Bonus</th>
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
                    <td className="numeric">
                      {row.totalTitles} ({row.feederTitles}F · {row.superleagueTitles}S)
                    </td>
                    <td className="numeric">{row.stageWins}</td>
                    <td className="numeric">{row.roundWins}</td>
                    <td className="numeric">{row.longestSuperleagueTenure}</td>
                    <td className="numeric">
                      {formatBonus(row.currentEffectiveBonusThousandths)}
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
                    <td>{record.label}</td>
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
            Colour Cup individual and Qualifier scoring, separated by event type and
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
            Colour Cup and Type Cup team scoring, separated by event type and
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
                <span className="badge badge-ready">{item.recordKey}</span>{' '}
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
                    <td>{row.honourKind}</td>
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
