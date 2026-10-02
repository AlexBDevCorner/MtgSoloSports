import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import { describeCupProgress, type TeamLiveBoardData } from './teamLive';
import './teamLive.css';

/** Display-only projection of fixed-point thousandths (no sporting math). */
function formatPoints(thousandths: number): string {
  return (thousandths / 1000).toFixed(3);
}

function medalLabel(medal: string | null): string {
  if (medal === 'Gold') {
    return '🥇 Gold';
  }
  if (medal === 'Silver') {
    return '🥈 Silver';
  }
  if (medal === 'Bronze') {
    return '🥉 Bronze';
  }
  return '—';
}

/**
 * Live team standings sidebar for one Cup team event (MSS-045). Renders every
 * participating team with the backend-authoritative running totals; rows are
 * never capped. Provisional boards carry an explicit badge and no medals; the
 * final board mirrors the official results exactly.
 */
export function TeamLiveBoard({
  data,
  loading,
  error,
  onRetry,
}: {
  data: TeamLiveBoardData | null;
  loading: boolean;
  error: string | null;
  onRetry: () => void;
}) {
  return (
    <Card
      eyebrow={data?.kind === 'color' ? 'Color Cup · live team standings' : 'Type Cup · live team standings'}
      title={data ? describeCupProgress(data) : 'Live team standings'}
      action={
        data ? (
          <span
            className={data.isProvisional ? 'badge badge-wait' : 'badge badge-done'}
            title={
              data.isProvisional
                ? 'Running totals from persisted rounds only; medals and final tie-breaks apply at completion.'
                : 'Official final team standings.'
            }
          >
            {data.isProvisional ? 'Provisional' : 'Final'}
          </span>
        ) : undefined
      }
    >
      {loading && !data ? <Loading label="Loading live standings…" /> : null}
      {error ? (
        <Notice tone="error" title="Live standings unavailable">
          <p>{error}</p>
          <p>
            <button type="button" className="ghost-button" onClick={onRetry}>
              Retry
            </button>
          </p>
        </Notice>
      ) : null}
      {!loading && !error && !data ? (
        <Notice tone="empty" title="No live standings yet">
          <p>Resolve the team selection for a completed season first, then play rounds.</p>
        </Notice>
      ) : null}
      {data ? (
        <>
          {data.isProvisional ? (
            <p className="muted small">
              Running team totals from all persisted rounds so far — completed groups plus
              rounds already played in Group {data.currentGroupNumber}. Order is informational
              (score, then team name); medals and official tie-breaks apply only at completion.
            </p>
          ) : (
            <p className="muted small">
              Official final totals
              {data.championName ? ` — ${data.championName} wins the team Cup` : ''}. Matches the
              persisted team results exactly.
            </p>
          )}
          <div className="table-wrap">
            <table className="data-table cup-live-table">
              <thead>
                <tr>
                  <th scope="col">Rank</th>
                  <th scope="col">Team</th>
                  <th scope="col">Score</th>
                  {data.isComplete ? <th scope="col">Medal</th> : null}
                </tr>
              </thead>
              <tbody>
                {data.rows.map((row) => (
                  <tr key={row.key}>
                    <td className="numeric">{row.rank}</td>
                    <td>{row.name}</td>
                    <td className="numeric">{formatPoints(row.scoreThousandths)}</td>
                    {data.isComplete ? <td>{medalLabel(row.medal)}</td> : null}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <p className="muted small">
            {data.rows.length} team{data.rows.length === 1 ? '' : 's'} · Season {data.sourceSeasonNumber} ·
            Group {data.currentGroupNumber} · {data.completedRounds}/{data.totalRounds} rounds persisted.
          </p>
        </>
      ) : null}
    </Card>
  );
}
