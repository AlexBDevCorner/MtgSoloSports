import { useEffect, useState } from 'react';
import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import { AthleteLink } from '../routing/router';
import {
  arrivalClass,
  balancedText,
  buildRebalanceRevealOrder,
  churnLine,
  churnSummary,
  movementGlyph,
  movementLabel,
  rosterCheckText,
  stepDescription,
  type RebalanceLeague,
  type RebalanceStep,
} from './rebalanceModel';
import type { RebalanceResult } from './rebalanceApi';
import './RebalanceReveal.css';

function detectReducedMotion(): boolean {
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') {
    return false;
  }
  return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
}

function TileArt({ step }: { step: RebalanceStep }) {
  const [imageFailed, setImageFailed] = useState(false);
  const imageUrl = step.imageUrl;

  useEffect(() => {
    setImageFailed(false);
  }, [imageUrl]);

  if (imageUrl && !imageFailed) {
    return (
      <img
        className="rebalance-tile-portrait"
        src={imageUrl}
        alt=""
        loading="lazy"
        draggable={false}
        onError={() => {
          setImageFailed(true);
        }}
      />
    );
  }
  return (
    <span className="rebalance-tile-portrait rebalance-portrait-fallback" aria-hidden="true">
      {step.name.slice(0, 2).toUpperCase()}
    </span>
  );
}

function MovementBadge({ step }: { step: RebalanceStep }) {
  return (
    <span className={`badge badge-rebalance-${step.movementType}`}>
      <span aria-hidden="true">{movementGlyph(step.movementType)} </span>
      {movementLabel(step.movementType)}
    </span>
  );
}

/**
 * Feeder-rebalance board for one season transition.
 *
 * Presentation-only over persisted backend facts: every tile renders the
 * authoritative source league, destination league and movement reason. Nothing
 * is resimulated and animation state lives in React only.
 *
 * Leagues reveal one at a time in alphabetical order; inside a league the
 * sporting sequence is departures to Superleague, returns from Superleague,
 * overflow to the common pool, then draws from the pool. A completed event
 * opens fully revealed so revisits land on the stable final state
 * immediately; "Replay the reveal" restarts the progressive board, which
 * advances one movement at a time (never on long timers). Unchanged leagues
 * render a compact "No changes" state and never consume reveal steps.
 */
export function RebalanceReveal({
  result,
  leagues,
  revealKey,
  title,
  meta,
  saveId,
}: {
  /** Authoritative persisted result (counts for the churn summary). */
  result: RebalanceResult;
  /** Leagues in reveal order (see `buildRebalanceLeagues`). */
  leagues: readonly RebalanceLeague[];
  /** Stable identity for the event (e.g. seasons + movement count). */
  revealKey: string;
  /** Human label such as "Season 2 → Season 3". */
  title: string;
  /** Optional meta line such as counts and provenance. */
  meta?: string;
  /** Save context for athlete profile links; when absent names render as text. */
  saveId?: string | null;
}) {
  const order = buildRebalanceRevealOrder(leagues);
  const total = order.length;
  const [revealedCount, setRevealedCount] = useState(total);
  const [prefersReducedMotion, setPrefersReducedMotion] = useState(false);

  // A new event identity restarts the presentation at the completed final
  // state; progressive stepping is opt-in via Replay. Animation state never
  // leaves React and never touches sporting state.
  useEffect(() => {
    setRevealedCount(total);
    // Restart only when the event identity changes; local stepping must not reset.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [revealKey]);

  useEffect(() => {
    if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') {
      return;
    }
    const query = window.matchMedia('(prefers-reduced-motion: reduce)');
    setPrefersReducedMotion(query.matches);
    const onChange = (event: MediaQueryListEvent) => {
      setPrefersReducedMotion(event.matches);
    };
    query.addEventListener('change', onChange);
    return () => {
      query.removeEventListener('change', onChange);
    };
  }, []);

  const churn = churnSummary(result, leagues);

  if (total === 0) {
    return (
      <Card eyebrow="Feeder rebalance" title={title}>
        <Notice tone="empty" title="No feeder changes">
          <p>Every feeder league stayed at 32 athletes with no Superleague or pool movement.</p>
        </Notice>
      </Card>
    );
  }

  const revealed = new Set(order.slice(0, revealedCount).map((step) => step.key));
  const isComplete = revealedCount >= total;
  const latest: RebalanceStep | null = revealedCount > 0 ? (order[revealedCount - 1] ?? null) : null;
  // The active league holds the next movement to reveal (or the latest one
  // once complete), so multi-league events keep their place obvious.
  const activeKey = latest?.key
    ? (leagues.find((league) => league.steps.some((step) => step.key === latest.key))?.key ?? null)
    : (leagues.find((league) => league.steps.some((step) => !revealed.has(step.key)))?.key ??
      leagues.find((league) => league.hasChanges)?.key ??
      null);

  function revealNext(): void {
    setRevealedCount((value) => Math.min(value + 1, total));
  }

  function revealLeague(): void {
    if (!activeKey) {
      return;
    }
    const active = leagues.find((league) => league.key === activeKey);
    if (!active) {
      return;
    }
    const lastKey = active.steps.length > 0 ? active.steps[active.steps.length - 1]!.key : null;
    let lastIndex = -1;
    for (let index = 0; index < order.length; index += 1) {
      if (order[index]!.key === lastKey) {
        lastIndex = index;
      }
    }
    if (lastIndex >= 0) {
      setRevealedCount(lastIndex + 1);
    }
  }

  function revealAll(): void {
    setRevealedCount(total);
  }

  function replay(): void {
    setRevealedCount(0);
  }

  return (
    <Card
      eyebrow="Feeder rebalance"
      title={`${title} — ${total} movement${total === 1 ? '' : 's'}`}
      info={
        <div>
          <p>
            Each feeder league starts at 32 athletes. Athletes leaving for the Superleague move
            upward out of the league; returning athletes move downward into it. Overflow athletes
            move down into the Common Pool and drawn athletes move up out of the pool into the
            league. Tiles land in their destination and stay there.
          </p>
          <p>
            The board replays persisted rebalance facts only and never resimulates. Pool draws
            show the exact athletes chosen by the backend; revisits reveal the same athletes.
            Reduced-motion preferences are respected: tiles appear without sliding and every
            result keeps its text label and direction.
          </p>
        </div>
      }
    >
      {meta ? <p className="muted small">{meta}</p> : null}
      <p className="muted small" aria-live="polite">
        {churnLine(churn)} · persisted result, never resimulated.
      </p>
      {prefersReducedMotion ? (
        <p className="muted small" role="note">
          Reduced-motion preference detected: tiles appear without sliding. Direction glyphs,
          movement labels and league names carry every result.
        </p>
      ) : null}

      <div className="rebalance-controls" role="group" aria-label="Reveal controls">
        <div className="live-buttons">
          {isComplete ? (
            <button type="button" className="ghost-button" onClick={replay}>
              Replay the reveal
            </button>
          ) : (
            <>
              <button type="button" className="primary-button" onClick={revealNext}>
                Reveal next
              </button>
              <button type="button" className="ghost-button" onClick={revealLeague}>
                Reveal league
              </button>
              <button type="button" className="ghost-button" onClick={revealAll}>
                Reveal all
              </button>
            </>
          )}
        </div>
      </div>

      <div
        className="reveal-progress"
        role="progressbar"
        aria-label="Rebalance reveal progress"
        aria-valuemin={0}
        aria-valuemax={total}
        aria-valuenow={revealedCount}
      >
        <div
          className="reveal-progress-bar"
          style={{ width: total === 0 ? '0%' : `${(revealedCount / total) * 100}%` }}
        />
      </div>
      <p className="muted small" aria-live="polite">
        {isComplete
          ? `All ${total} persisted movements revealed — final state, nothing is recalculated.`
          : `Revealed ${revealedCount}/${total} persisted movements — presentation only, nothing is recalculated.`}
      </p>
      {latest ? (
        <p className="muted small" aria-live="polite">
          Latest: {stepDescription(latest)}
        </p>
      ) : (
        <p className="muted small" aria-live="polite">
          The board is face-down. Step with Reveal next — departures reveal first within each
          league, then returns, then pool outflow, then pool draws.
        </p>
      )}

      <div className="rebalance-leagues">
        {leagues.map((league) => {
          const leagueRevealed = league.steps.filter((step) => revealed.has(step.key)).length;
          const isActive = league.key === activeKey;
          if (!league.hasChanges) {
            return (
              <section
                key={league.key}
                className="rebalance-league is-unchanged"
                aria-label={`${league.leagueName} unchanged`}
              >
                <header className="rebalance-league-head">
                  <h3 className="rebalance-league-title">{league.leagueName}</h3>
                  <p className="muted small">No changes · 32 / 32 — BALANCED</p>
                </header>
                <p className="muted small">
                  No Superleague or pool movement for this league. It stayed at 32 athletes and
                  needs no reveal steps.
                </p>
              </section>
            );
          }
          const departedRevealed = league.departed.filter((step) => revealed.has(step.key));
          const departedPending = league.departed.filter((step) => !revealed.has(step.key));
          const returnedRevealed = league.returned.filter((step) => revealed.has(step.key));
          const returnedPending = league.returned.filter((step) => !revealed.has(step.key));
          const displacedRevealed = league.displaced.filter((step) => revealed.has(step.key));
          const displacedPending = league.displaced.filter((step) => !revealed.has(step.key));
          const drawnRevealed = league.drawn.filter((step) => revealed.has(step.key));
          const drawnPending = league.drawn.filter((step) => !revealed.has(step.key));
          return (
            <section
              key={league.key}
              id={`rebalance-league-${league.key}`}
              className={isActive ? 'rebalance-league is-active' : 'rebalance-league'}
              aria-current={isActive ? 'step' : undefined}
              aria-label={`${league.leagueName} rebalance`}
            >
              <header className="rebalance-league-head">
                <h3 className="rebalance-league-title">
                  {league.leagueName} · {league.sportingColor}
                </h3>
                <p className="muted small">
                  {league.departed.length} to Superleague · {league.returned.length} returning ·{' '}
                  {league.displaced.length} to pool · {league.drawn.length} drawn ·{' '}
                  {leagueRevealed}/{league.total} revealed
                  {isActive ? ' · active league' : ''}
                </p>
              </header>

              <p className="rebalance-counts" aria-live="polite">
                <span className="rebalance-count">
                  Start <strong>32 / 32</strong>
                </span>
                <span className="rebalance-count-sep" aria-hidden="true">
                  →
                </span>
                <span className="rebalance-count">
                  After Superleague <strong>{rosterCheckText(league)}</strong>
                </span>
                <span className="rebalance-count-sep" aria-hidden="true">
                  →
                </span>
                <span className="rebalance-count">
                  Final <strong>{balancedText(league)}</strong>
                </span>
              </p>

              <div className="rebalance-phases">
                <div className="rebalance-zone rebalance-zone-super">
                  <p className="rebalance-zone-label">To Superleague · upward out of the league</p>
                  {league.departed.length === 0 ? (
                    <p className="muted small">No departures for this league.</p>
                  ) : (
                    <ol className="rebalance-tiles" aria-label={`${league.leagueName} departures`}>
                      {departedRevealed.map((step) => (
                        <RebalanceTile
                          key={step.key}
                          step={step}
                          sequence={order.findIndex((entry) => entry.key === step.key) + 1}
                          total={total}
                          isLatest={latest?.key === step.key}
                          saveId={saveId}
                        />
                      ))}
                      {departedPending.map((step) => (
                        <PendingTile
                          key={step.key}
                          step={step}
                          sequence={order.findIndex((entry) => entry.key === step.key) + 1}
                        />
                      ))}
                    </ol>
                  )}
                </div>

                <div className="rebalance-zone">
                  <p className="rebalance-zone-label">Returning from Superleague · downward into the league</p>
                  {league.returned.length === 0 ? (
                    <p className="muted small">No returning athletes for this league.</p>
                  ) : (
                    <ol className="rebalance-tiles" aria-label={`${league.leagueName} returns`}>
                      {returnedRevealed.map((step) => (
                        <RebalanceTile
                          key={step.key}
                          step={step}
                          sequence={order.findIndex((entry) => entry.key === step.key) + 1}
                          total={total}
                          isLatest={latest?.key === step.key}
                          saveId={saveId}
                        />
                      ))}
                      {returnedPending.map((step) => (
                        <PendingTile
                          key={step.key}
                          step={step}
                          sequence={order.findIndex((entry) => entry.key === step.key) + 1}
                        />
                      ))}
                    </ol>
                  )}
                  <p className="muted small">
                    Roster check after Superleague movement: <strong>{rosterCheckText(league)}</strong>
                    {league.needsPoolAdjustment
                      ? league.provisionalCount > 32
                        ? ' — overfilled, the lowest-ranked athletes must leave for the pool.'
                        : ' — underfilled, open slots must be drawn from the pool.'
                      : ' — already 32 / 32, no pool adjustment needed.'}
                  </p>
                </div>

                <div className="rebalance-zone rebalance-zone-pool">
                  <p className="rebalance-zone-label">
                    Common Pool · shared side area, only affected athletes shown
                  </p>
                  {!league.needsPoolAdjustment ? (
                    <p className="muted small">
                      No pool adjustment — the league reached exactly 32 after Superleague
                      movement.
                    </p>
                  ) : null}
                  {league.displaced.length > 0 ? (
                    <>
                      <p className="muted small">
                        Overflow: the lowest-ranked remaining athletes leave for the pool one by
                        one.
                      </p>
                      <ol className="rebalance-tiles" aria-label={`${league.leagueName} to pool`}>
                        {displacedRevealed.map((step) => (
                          <RebalanceTile
                            key={step.key}
                            step={step}
                            sequence={order.findIndex((entry) => entry.key === step.key) + 1}
                            total={total}
                            isLatest={latest?.key === step.key}
                            saveId={saveId}
                          />
                        ))}
                        {displacedPending.map((step) => (
                          <PendingTile
                            key={step.key}
                            step={step}
                            sequence={order.findIndex((entry) => entry.key === step.key) + 1}
                          />
                        ))}
                      </ol>
                    </>
                  ) : null}
                  {league.drawn.length > 0 ? (
                    <>
                      <p className="muted small">
                        {league.displaced.length === 0
                          ? `Open slots first, then the exact backend selections enter one by one.`
                          : `Draws enter one by one.`}{' '}
                        Identities stay face-down until revealed; the backend result is already
                        fixed and revisits show the same athletes.
                      </p>
                      <ol className="rebalance-tiles" aria-label={`${league.leagueName} drawn from pool`}>
                        {drawnRevealed.map((step) => (
                          <RebalanceTile
                            key={step.key}
                            step={step}
                            sequence={order.findIndex((entry) => entry.key === step.key) + 1}
                            total={total}
                            isLatest={latest?.key === step.key}
                            saveId={saveId}
                          />
                        ))}
                        {drawnPending.map((step) => (
                          <PendingTile
                            key={step.key}
                            step={step}
                            sequence={order.findIndex((entry) => entry.key === step.key) + 1}
                          />
                        ))}
                      </ol>
                    </>
                  ) : null}
                </div>

                <p className="rebalance-balanced" aria-live="polite">
                  {league.steps.every((step) => revealed.has(step.key)) ? (
                    <span>
                      <strong>{balancedText(league)}</strong> — every movement for this league is
                      revealed.
                    </span>
                  ) : (
                    <span className="muted small">
                      Final target <strong>32 / 32 — BALANCED</strong>; keep revealing to reach it.
                    </span>
                  )}
                </p>
              </div>
            </section>
          );
        })}
      </div>

      {isComplete ? (
        <>
          <h3 className="reveal-subhead">Final summary — all feeder changes</h3>
          <div className="table-wrap">
            <table className="data-table rebalance-summary">
              <thead>
                <tr>
                  <th scope="col">League</th>
                  <th scope="col">Movement</th>
                  <th scope="col">Card</th>
                  <th scope="col">Old league → new league</th>
                </tr>
              </thead>
              <tbody>
                {order.map((step) => {
                  const league = leagues.find((entry) =>
                    entry.steps.some((candidate) => candidate.key === step.key),
                  );
                  return (
                    <tr key={step.key}>
                      <td>{league?.leagueName ?? '—'}</td>
                      <td>
                        <MovementBadge step={step} />
                      </td>
                      <td>
                        {saveId ? (
                          <AthleteLink saveId={saveId} athleteId={step.athleteId} name={step.name} />
                        ) : (
                          <span className="card-name">{step.name}</span>
                        )}
                        {step.fromSeasonRank > 0 ? (
                          <span className="card-sub"> · P{step.fromSeasonRank} last season</span>
                        ) : (
                          <span className="card-sub"> · pool draw</span>
                        )}
                      </td>
                      <td>
                        {step.fromLeagueName} → {step.toLeagueName}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
          <h3 className="reveal-subhead">Per-league counts</h3>
          <div className="table-wrap">
            <table className="data-table rebalance-counts-table">
              <thead>
                <tr>
                  <th scope="col">League</th>
                  <th scope="col">To Superleague</th>
                  <th scope="col">Returning</th>
                  <th scope="col">To pool</th>
                  <th scope="col">Drawn</th>
                  <th scope="col">Final</th>
                </tr>
              </thead>
              <tbody>
                {leagues.map((league) => (
                  <tr key={league.key}>
                    <td>{league.leagueName}</td>
                    <td className="numeric">{league.departed.length}</td>
                    <td className="numeric">{league.returned.length}</td>
                    <td className="numeric">{league.displaced.length}</td>
                    <td className="numeric">{league.drawn.length}</td>
                    <td className="numeric">{balancedText(league)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      ) : null}
    </Card>
  );
}

function RebalanceTile({
  step,
  sequence,
  total,
  isLatest,
  saveId,
}: {
  step: RebalanceStep;
  sequence: number;
  total: number;
  isLatest: boolean;
  saveId?: string | null;
}) {
  const arrive = arrivalClass(step.movementType);
  return (
    <li
      className={
        `rebalance-tile is-${step.movementType}` + (isLatest ? ` is-latest ${arrive}` : '')
      }
      data-athlete-id={step.athleteId}
      data-revealed="true"
      aria-label={`Move ${sequence} of ${total}: ${stepDescription(step)}`}
    >
      <div className="rebalance-tile-top">
        <span className="rebalance-seq">#{sequence}</span>
        <MovementBadge step={step} />
      </div>
      <div className="rebalance-tile-art">
        <TileArt step={step} />
      </div>
      <div className="rebalance-tile-body">
        {saveId ? (
          <AthleteLink
            saveId={saveId}
            athleteId={step.athleteId}
            name={step.name}
            className="card-name card-link"
          />
        ) : (
          <span className="card-name">{step.name}</span>
        )}
        <span className="card-sub">
          {step.sportingColor}
          {step.fromSeasonRank > 0 ? ` · P${step.fromSeasonRank} last season` : ' · pool draw'}
        </span>
        <span className="rebalance-leagues">
          {step.fromLeagueName} → {step.toLeagueName}
        </span>
      </div>
    </li>
  );
}

function PendingTile({ step, sequence }: { step: RebalanceStep; sequence: number }) {
  return (
    <li
      className="rebalance-tile is-pending"
      data-revealed="false"
      aria-label={`Move ${sequence} not revealed yet`}
    >
      <div className="rebalance-tile-top">
        <span className="rebalance-seq">#{sequence}</span>
        <span className="rebalance-pending-badge">Face down</span>
      </div>
      <div className="rebalance-tile-art">
        <span className="rebalance-card-back" aria-hidden="true">
          <span className="rebalance-card-back-motif">
            {step.movementType === 'to-superleague' || step.movementType === 'drawn' ? '▲' : '▼'}
          </span>
          <span className="rebalance-card-back-text">Face down</span>
        </span>
      </div>
      <div className="rebalance-tile-body">
        <span className="muted small">Not revealed yet</span>
      </div>
    </li>
  );
}

export function RebalanceRevealLoading({ title }: { title: string }) {
  return (
    <Card eyebrow="Feeder rebalance" title={title}>
      <Loading label="Loading feeder rebalance…" />
    </Card>
  );
}
