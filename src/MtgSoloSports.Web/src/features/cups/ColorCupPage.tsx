import { useCallback, useEffect, useState } from 'react';
import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import { ApiError, apiErrorMessage } from '../../shared/api/http';
import {
  fetchColorCupIndividual,
  runColorCupIndividual,
  type ColorCupIndividualResult,
} from './colorCupApi';

/** Display-only projection of fixed-point thousandths (no sporting math). */
function formatPoints(thousandths: number): string {
  return (thousandths / 1000).toFixed(3);
}

function medalBadge(medal: string): string {
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

export function ColorCupPage({
  saveId,
  hasSelection,
  onGoToSaves,
  onSelectAthlete,
}: {
  saveId: string | null;
  hasSelection: boolean;
  onGoToSaves: () => void;
  onSelectAthlete: (athleteId: number) => void;
}) {
  const [result, setResult] = useState<ColorCupIndividualResult | null>(null);
  const [loading, setLoading] = useState(false);
  const [running, setRunning] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notFound, setNotFound] = useState(false);

  const load = useCallback(
    async (signal: AbortSignal) => {
      if (!saveId) {
        return;
      }
      setLoading(true);
      setError(null);
      try {
        const loaded = await fetchColorCupIndividual(saveId, null, signal);
        setResult(loaded);
        setNotFound(false);
      } catch (failure: unknown) {
        if (failure instanceof DOMException && failure.name === 'AbortError') {
          return;
        }
        if (failure instanceof ApiError && failure.status === 404) {
          setResult(null);
          setNotFound(true);
        } else {
          setError(apiErrorMessage(failure));
        }
      } finally {
        setLoading(false);
      }
    },
    [saveId],
  );

  useEffect(() => {
    if (!saveId) {
      setResult(null);
      setLoading(false);
      setError(null);
      setNotFound(false);
      return;
    }
    const controller = new AbortController();
    void load(controller.signal);
    return () => {
      controller.abort();
    };
  }, [saveId, load]);

  const handleRun = useCallback(async () => {
    if (!saveId) {
      return;
    }
    setRunning(true);
    setError(null);
    try {
      const completed = await runColorCupIndividual(saveId, null);
      setResult(completed);
      setNotFound(false);
    } catch (failure: unknown) {
      if (failure instanceof ApiError && failure.status === 409) {
        setError(apiErrorMessage(failure));
        const controller = new AbortController();
        await load(controller.signal);
      } else {
        setError(apiErrorMessage(failure));
      }
    } finally {
      setRunning(false);
    }
  }, [saveId, load]);

  if (!hasSelection || !saveId) {
    return (
      <Notice tone="empty" title="No save selected">
        <p>Pick a universe on the Saves tab to view the Color Cup.</p>
        <p>
          <button type="button" className="primary-button" onClick={onGoToSaves}>
            Go to saves
          </button>
        </p>
      </Notice>
    );
  }

  if (loading && !result) {
    return <Loading label="Loading Color Cup…" />;
  }

  const standings = result?.standings ?? [];
  const podium = standings.slice(0, 3);

  return (
    <div className="dashboard">
      <Card
        eyebrow="Color Cup · individual"
        title={
          result
            ? `Season ${result.sourceSeasonNumber} — ${result.championName} wins the Cup`
            : 'Color Cup individual event'
        }
        action={
          loading ? <span className="muted small">Refreshing…</span> : undefined
        }
      >
        {error ? (
          <Notice tone="error" title="Color Cup unavailable">
            <p>{error}</p>
          </Notice>
        ) : null}
        {result ? (
          <>
            <p className="muted small">
              32 selected athletes · {result.rounds} rounds · checksum{' '}
              <code title={result.checksum}>
                {result.checksum.length > 12
                  ? `${result.checksum.slice(0, 12)}…`
                  : result.checksum}
              </code>{' '}
              · active bonus applies, no new bonus or league points.
            </p>
            <div className="table-wrap">
              <table className="data-table">
                <thead>
                  <tr>
                    <th scope="col">Medal</th>
                    <th scope="col">Rank</th>
                    <th scope="col">Card</th>
                  </tr>
                </thead>
                <tbody>
                  {podium.map((row) => (
                    <tr key={row.athleteId}>
                      <td>{medalBadge(row.medal)}</td>
                      <td className="numeric">{row.cupRank}</td>
                      <td>
                        <button
                          type="button"
                          className="card-name card-link"
                          title={`Open career profile for ${row.name}`}
                          onClick={() => {
                            onSelectAthlete(row.athleteId);
                          }}
                        >
                          {row.name}
                        </button>
                        <span className="card-sub">
                          {' '}
                          · {row.sportingColor} · #{row.selectionRank}
                        </span>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </>
        ) : notFound ? (
          <p className="muted">
            No Color Cup individual result yet. Resolve the Color Cup team
            selection for a completed odd season first, then run the 16-round
            individual event.
          </p>
        ) : null}
        <p>
          <button
            type="button"
            className="primary-button"
            disabled={running}
            onClick={() => {
              void handleRun();
            }}
          >
            {running ? 'Running…' : 'Run Color Cup individual'}
          </button>
        </p>
        <p className="muted small">
          Runs the 32 selected athletes through one standard 16-round stage.
          Exact round payloads persist for replay; career bonus never changes.
        </p>
      </Card>

      <Card
        eyebrow="Standings"
        title={result ? `Full table — ${standings.length} athletes` : 'Full table'}
      >
        {!result ? (
          <p className="muted">Run the Cup to populate the full table.</p>
        ) : (
          <div className="table-wrap">
            <table className="data-table">
              <thead>
                <tr>
                  <th scope="col">Rank</th>
                  <th scope="col">Card</th>
                  <th scope="col">Cup score</th>
                  <th scope="col">Round W</th>
                  <th scope="col">Medal</th>
                </tr>
              </thead>
              <tbody>
                {standings.map((row) => (
                  <tr key={row.athleteId}>
                    <td className="numeric">{row.cupRank}</td>
                    <td>
                      <button
                        type="button"
                        className="card-name card-link"
                        title={`Open career profile for ${row.name}`}
                        onClick={() => {
                          onSelectAthlete(row.athleteId);
                        }}
                      >
                        {row.name}
                      </button>
                      <span className="card-sub">
                        {' '}
                        · {row.sportingColor} · #{row.selectionRank}
                      </span>
                    </td>
                    <td className="numeric">
                      {formatPoints(row.cupScoreThousandths)}
                    </td>
                    <td className="numeric">{row.roundWins}</td>
                    <td>{medalBadge(row.medal)}</td>
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
