import { useEffect, useState } from 'react';
import { ApiError, apiErrorMessage } from '../../shared/api/http';
import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import { Link } from '../routing/router';
import { dashboardPath } from '../routing/routes';
import { buildRebalanceLeagues } from './rebalanceModel';
import { fetchRebalanceResult, type RebalanceResult } from './rebalanceApi';
import { RebalanceReveal } from './RebalanceReveal';

/**
 * Feeder-rebalance section for the Standings page.
 *
 * Reads the authoritative persisted rebalance for the selected source season,
 * pinned to `fromSeasonNumber`. Pinning keeps historical revisits stable:
 * reopening Season N always renders Season N's rebalance, never the athlete's
 * current league. Presentation only; the reveal consumes these facts without
 * resimulating or rerolling pool draws.
 */
export function RebalanceSection({
  saveId,
  seasonNumber,
  isSeasonComplete,
}: {
  saveId: string;
  seasonNumber: number;
  /** True once the selected season's final tables are persisted. */
  isSeasonComplete: boolean;
}) {
  const [result, setResult] = useState<RebalanceResult | null>(null);
  const [loading, setLoading] = useState(true);
  const [notResolved, setNotResolved] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    const { signal } = controller;
    setResult(null);
    setLoading(true);
    setNotResolved(false);
    setError(null);

    async function load(): Promise<void> {
      try {
        const resolved = await fetchRebalanceResult(saveId, seasonNumber, signal);
        setResult(resolved);
        setLoading(false);
      } catch (failure: unknown) {
        if (failure instanceof DOMException && failure.name === 'AbortError') {
          return;
        }
        if (failure instanceof ApiError && failure.status === 404) {
          setNotResolved(true);
        } else {
          setError(apiErrorMessage(failure));
        }
        setLoading(false);
      }
    }

    void load();
    return () => controller.abort();
  }, [saveId, seasonNumber]);

  if (loading) {
    return (
      <Card eyebrow="Feeder rebalance" title={`Season ${seasonNumber} feeder rebalance`}>
        <Loading label="Loading feeder rebalance…" />
      </Card>
    );
  }

  if (error) {
    return (
      <Card eyebrow="Feeder rebalance" title={`Season ${seasonNumber} feeder rebalance`}>
        <Notice tone="error" title="Feeder rebalance unavailable">
          <p>{error}</p>
        </Notice>
      </Card>
    );
  }

  if (notResolved || !result) {
    // Mid-postseason edge state: final tables exist but rebalancing has not
    // run yet. In-progress seasons stay quiet instead.
    if (!isSeasonComplete) {
      return null;
    }
    return (
      <Card eyebrow="Feeder rebalance" title={`Season ${seasonNumber} feeder rebalance`}>
        <Notice tone="info" title="Feeder rebalance not resolved yet">
          <p>
            The final tables are persisted but no feeder rebalance exists for Season{' '}
            {seasonNumber} yet.
          </p>
          <p className="live-buttons">
            <Link to={dashboardPath(saveId)} className="primary-button">
              Continue the postseason on the Dashboard
            </Link>
          </p>
        </Notice>
      </Card>
    );
  }

  const leagues = buildRebalanceLeagues(result);
  const changed = leagues.filter((league) => league.hasChanges).length;
  return (
    <RebalanceReveal
      result={result}
      leagues={leagues}
      revealKey={`rebalance:${result.fromSeasonNumber}:${result.toSeasonNumber}:${result.movementCount}:${result.totalDeparted}:${result.totalReturned}`}
      title={`Season ${result.fromSeasonNumber} → Season ${result.toSeasonNumber} · feeder rebalance`}
      meta={`${leagues.length} feeder leagues · ${changed} changed · ${result.totalDeparted} to Superleague · ${result.totalReturned} returning · ${result.totalDisplaced} to pool · ${result.totalDrawn} drawn · persisted result, never resimulated.`}
      saveId={saveId}
      initialMode="complete"
    />
  );
}
