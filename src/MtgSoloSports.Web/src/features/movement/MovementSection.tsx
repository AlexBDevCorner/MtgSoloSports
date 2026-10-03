import { useEffect, useState } from 'react';
import { ApiError, apiErrorMessage } from '../../shared/api/http';
import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import { Link } from '../routing/router';
import { dashboardPath } from '../routing/routes';
import {
  boundariesForInaugural,
  boundariesForMovement,
} from './movementModel';
import {
  fetchAutomaticMovement,
  fetchInauguralRoster,
  type AutomaticMovement,
  type InauguralRoster,
} from './movementApi';
import { MovementReveal } from './MovementReveal';

/**
 * Promotion/relegation section for the Standings page.
 *
 * Reads the authoritative persisted transition for the selected season:
 * Season 1 resolves through the inaugural roster (promotions only), later
 * seasons through automatic movement pinned to `fromSeasonNumber`. Pinning
 * the source season keeps historical revisits stable: reopening Season N
 * always renders Season N's movements, never the athlete's current league.
 * Presentation only; the reveal consumes these facts without resimulating.
 */
export function MovementSection({
  saveId,
  seasonNumber,
  isSeasonComplete,
}: {
  saveId: string;
  seasonNumber: number;
  /** True once the selected season's final tables are persisted. */
  isSeasonComplete: boolean;
}) {
  const [movement, setMovement] = useState<AutomaticMovement | null>(null);
  const [inaugural, setInaugural] = useState<InauguralRoster | null>(null);
  const [loading, setLoading] = useState(true);
  const [notResolved, setNotResolved] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    const { signal } = controller;
    setMovement(null);
    setInaugural(null);
    setLoading(true);
    setNotResolved(false);
    setError(null);

    async function load(): Promise<void> {
      try {
        if (seasonNumber === 1) {
          const roster = await fetchInauguralRoster(saveId, signal);
          setInaugural(roster);
        } else {
          const resolved = await fetchAutomaticMovement(saveId, seasonNumber, signal);
          setMovement(resolved);
        }
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
      <Card eyebrow="Promotion & relegation" title={`Season ${seasonNumber} league movements`}>
        <Loading label="Loading league movements…" />
      </Card>
    );
  }

  if (error) {
    return (
      <Card eyebrow="Promotion & relegation" title={`Season ${seasonNumber} league movements`}>
        <Notice tone="error" title="League movements unavailable">
          <p>{error}</p>
        </Notice>
      </Card>
    );
  }

  if (notResolved || (!movement && !inaugural)) {
    // Mid-postseason edge state: final tables exist but the movement step has
    // not run yet. In-progress seasons stay quiet instead.
    if (!isSeasonComplete) {
      return null;
    }
    return (
      <Card eyebrow="Promotion & relegation" title={`Season ${seasonNumber} league movements`}>
        <Notice tone="info" title="Promotion & relegation not resolved yet">
          <p>
            The final tables are persisted but no promotion or relegation movements exist for
            Season {seasonNumber} yet.
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

  if (inaugural) {
    const boundaries = boundariesForInaugural(inaugural.members, inaugural.superleagueLeagueName);
    return (
      <MovementReveal
        boundaries={boundaries}
        revealKey={`inaugural:${inaugural.seasonNumber}:${inaugural.movementCount}`}
        title={`Season 1 → Season 2 · inaugural Superleague`}
        meta={`${boundaries.length} feeder boundaries · ${inaugural.members.length} promoted athletes · persisted roster, never resimulated.`}
        saveId={saveId}
      />
    );
  }

  if (movement) {
    const boundaries = boundariesForMovement(movement);
    return (
      <MovementReveal
        boundaries={boundaries}
        revealKey={`movement:${movement.fromSeasonNumber}:${movement.toSeasonNumber}:${movement.movementCount}`}
        title={`Season ${movement.fromSeasonNumber} → Season ${movement.toSeasonNumber}`}
        meta={`${boundaries.length} league boundaries · ${movement.promoted.length} promoted · ${movement.relegated.length} relegated · persisted movements, never resimulated.`}
        saveId={saveId}
      />
    );
  }

  return null;
}
