import { useEffect, useState } from 'react';
import { ApiError, apiErrorMessage } from '../../shared/api/http';
import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import { TRANSITION_TITLES, type TransitionKey } from '../events/eventModel';
import { Link } from '../routing/router';
import { dashboardPath, standingsPath } from '../routing/routes';
import { boundariesForInaugural, boundariesForMovement } from '../movement/movementModel';
import {
  fetchAutomaticMovement,
  fetchInauguralRoster,
  type AutomaticMovement,
  type InauguralRoster,
} from '../movement/movementApi';
import { MovementReveal } from '../movement/MovementReveal';
import { buildRebalanceLeagues } from '../rebalance/rebalanceModel';
import { fetchRebalanceResult, type RebalanceResult } from '../rebalance/rebalanceApi';
import { RebalanceReveal } from '../rebalance/RebalanceReveal';
import '../live/LivePage.css';

/**
 * MSS-053 first-time transition reveal on Live.
 *
 * Read-only presentation over the already-persisted postseason transition for
 * `season` (the source season): promotion/relegation — including the Season 1
 * inaugural Superleague formation — or the feeder-league rebalance. The
 * Dashboard persists the sporting result exactly once before navigating here,
 * so this view never mutates: it re-reads the persisted facts on mount, which
 * keeps refresh and Back/Forward safe without duplicating movement or
 * rebalance rows. The reveal starts face-down (`initialMode="reveal"`) for
 * deliberate stepping; historical viewing on Standings stays fully revealed.
 * Continuing is an explicit link back to the Dashboard, where the next legal
 * backend action is already waiting. This view never runs that next step.
 */
export function TransitionRevealView({
  saveId,
  transition,
  season,
}: {
  saveId: string;
  transition: TransitionKey;
  /** Source season of the persisted transition (e.g. 2 for Season 2 → 3). */
  season: number;
}) {
  if (transition === 'rebalance') {
    return <RebalanceTransition saveId={saveId} season={season} />;
  }
  return <MovementTransition saveId={saveId} season={season} />;
}

function TransitionSidebar({
  title,
  detail,
  saveId,
}: {
  title: string;
  detail: string;
  saveId: string;
}) {
  return (
    <aside className="live-sidebar" aria-label="Transition management">
      <Card eyebrow="Postseason transition" title={title}>
        <p className="muted small live-count">{detail}</p>
        <p className="muted small">
          The result is already saved and can&apos;t be undone. Step through the reveal below, then
          continue — the next legal postseason step is already waiting on the Dashboard.
        </p>
        <p className="live-buttons">
          <Link to={dashboardPath(saveId)} className="primary-button">
            Continue on the Dashboard
          </Link>
          <Link to={standingsPath(saveId)} className="ghost-button">
            Standings
          </Link>
        </p>
        <p className="muted small">{"Finishing or skipping the reveal never runs the next step."}</p>
      </Card>
    </aside>
  );
}

function TransitionContinue({ saveId }: { saveId: string }) {
  return (
    <Card eyebrow="Postseason" title="Back to the postseason">
      <p className="muted small">
        The reveal above replays persisted results only. Return to the Dashboard for the next legal
        step — nothing else runs until then.
      </p>
      <p className="live-buttons">
        <Link to={dashboardPath(saveId)} className="primary-button">
          Continue on the Dashboard
        </Link>
        <Link to={standingsPath(saveId)} className="ghost-button">
          View standings
        </Link>
      </p>
    </Card>
  );
}

function NotResolved({ saveId, season, transition }: { saveId: string; season: number; transition: TransitionKey }) {
  return (
    <Card eyebrow="Postseason transition" title={`${TRANSITION_TITLES[transition]} · Season ${season}`}>
      <Notice tone="info" title="Transition not resolved yet">
        <p>
          No persisted {TRANSITION_TITLES[transition].toLowerCase()} result exists for Season{' '}
          {season} yet. Resolve the step from the Dashboard first.
        </p>
        <p className="live-buttons">
          <Link to={dashboardPath(saveId)} className="primary-button">
            Continue on the Dashboard
          </Link>
        </p>
      </Notice>
    </Card>
  );
}

function MovementTransition({ saveId, season }: { saveId: string; season: number }) {
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
        if (season === 1) {
          const roster = await fetchInauguralRoster(saveId, signal);
          setInaugural(roster);
        } else {
          const resolved = await fetchAutomaticMovement(saveId, season, signal);
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
  }, [saveId, season]);

  if (loading) {
    return <Loading label="Loading league movements…" />;
  }

  if (error) {
    return (
      <Card eyebrow="Postseason transition" title={`Promotion & relegation · Season ${season}`}>
        <Notice tone="error" title="League movements unavailable">
          <p>{error}</p>
          <p className="live-buttons">
            <Link to={dashboardPath(saveId)} className="ghost-button">
              Back to the Dashboard
            </Link>
          </p>
        </Notice>
      </Card>
    );
  }

  if (notResolved || (!movement && !inaugural)) {
    return <NotResolved saveId={saveId} season={season} transition="movement" />;
  }

  if (inaugural) {
    const boundaries = boundariesForInaugural(inaugural.members, inaugural.superleagueLeagueName);
    return (
      <div className="live-layout">
        <TransitionSidebar
          title="Inaugural Superleague · Season 1 → Season 2"
          detail={`${boundaries.length} feeder boundaries · ${inaugural.members.length} promoted athletes · persisted roster, never resimulated.`}
          saveId={saveId}
        />
        <section className="live-main" aria-label="Inaugural Superleague reveal">
          <MovementReveal
            boundaries={boundaries}
            revealKey={`inaugural:${inaugural.seasonNumber}:${inaugural.movementCount}`}
            title="Season 1 → Season 2 · inaugural Superleague"
            meta={`${boundaries.length} feeder boundaries · ${inaugural.members.length} promoted athletes · persisted roster, never resimulated.`}
            saveId={saveId}
            initialMode="reveal"
          />
          <TransitionContinue saveId={saveId} />
        </section>
      </div>
    );
  }

  const boundaries = boundariesForMovement(movement!);
  return (
    <div className="live-layout">
      <TransitionSidebar
        title={`Promotion & relegation · Season ${movement!.fromSeasonNumber} → Season ${movement!.toSeasonNumber}`}
        detail={`${boundaries.length} league boundaries · ${movement!.promoted.length} promoted · ${movement!.relegated.length} relegated · persisted movements, never resimulated.`}
        saveId={saveId}
      />
      <section className="live-main" aria-label="Promotion and relegation reveal">
        <MovementReveal
          boundaries={boundaries}
          revealKey={`movement:${movement!.fromSeasonNumber}:${movement!.toSeasonNumber}:${movement!.movementCount}`}
          title={`Season ${movement!.fromSeasonNumber} → Season ${movement!.toSeasonNumber}`}
          meta={`${boundaries.length} league boundaries · ${movement!.promoted.length} promoted · ${movement!.relegated.length} relegated · persisted movements, never resimulated.`}
          saveId={saveId}
          initialMode="reveal"
        />
        <TransitionContinue saveId={saveId} />
      </section>
    </div>
  );
}

function RebalanceTransition({ saveId, season }: { saveId: string; season: number }) {
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
        const resolved = await fetchRebalanceResult(saveId, season, signal);
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
  }, [saveId, season]);

  if (loading) {
    return <Loading label="Loading feeder rebalance…" />;
  }

  if (error) {
    return (
      <Card eyebrow="Postseason transition" title={`Feeder rebalance · Season ${season}`}>
        <Notice tone="error" title="Feeder rebalance unavailable">
          <p>{error}</p>
          <p className="live-buttons">
            <Link to={dashboardPath(saveId)} className="ghost-button">
              Back to the Dashboard
            </Link>
          </p>
        </Notice>
      </Card>
    );
  }

  if (notResolved || !result) {
    return <NotResolved saveId={saveId} season={season} transition="rebalance" />;
  }

  const leagues = buildRebalanceLeagues(result);
  const changed = leagues.filter((league) => league.hasChanges).length;
  return (
    <div className="live-layout">
      <TransitionSidebar
        title={`Feeder rebalance · Season ${result.fromSeasonNumber} → Season ${result.toSeasonNumber}`}
        detail={`${leagues.length} feeder leagues · ${changed} changed · ${result.totalDeparted} to Superleague · ${result.totalReturned} returning · ${result.totalDisplaced} to pool · ${result.totalDrawn} drawn · persisted result, never resimulated.`}
        saveId={saveId}
      />
      <section className="live-main" aria-label="Feeder rebalance reveal">
        <RebalanceReveal
          result={result}
          leagues={leagues}
          revealKey={`rebalance:${result.fromSeasonNumber}:${result.toSeasonNumber}:${result.movementCount}:${result.totalDeparted}:${result.totalReturned}`}
          title={`Season ${result.fromSeasonNumber} → Season ${result.toSeasonNumber} · feeder rebalance`}
          meta={`${leagues.length} feeder leagues · ${changed} changed · ${result.totalDeparted} to Superleague · ${result.totalReturned} returning · ${result.totalDisplaced} to pool · ${result.totalDrawn} drawn · persisted result, never resimulated.`}
          saveId={saveId}
          initialMode="reveal"
        />
        <TransitionContinue saveId={saveId} />
      </section>
    </div>
  );
}
