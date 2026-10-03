import { useEffect, useState } from 'react';
import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import { AthleteLink } from '../routing/router';
import {
  buildRevealOrder,
  directionGlyph,
  directionLabel,
  stepDescription,
  type MovementBoundary,
  type MovementDirection,
  type MovementStep,
} from './movementModel';
import './MovementReveal.css';

function detectReducedMotion(): boolean {
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') {
    return false;
  }
  return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
}

function TileArt({ step }: { step: MovementStep }) {
  const [imageFailed, setImageFailed] = useState(false);
  const imageUrl = step.imageUrl;

  useEffect(() => {
    setImageFailed(false);
  }, [imageUrl]);

  if (imageUrl && !imageFailed) {
    return (
      <img
        className="movement-tile-portrait"
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
    <span className="movement-tile-portrait movement-portrait-fallback" aria-hidden="true">
      {step.name.slice(0, 2).toUpperCase()}
    </span>
  );
}

function MovementBadge({ direction }: { direction: MovementDirection }) {
  return (
    <span className={direction === 'promoted' ? 'badge badge-promoted' : 'badge badge-relegated'}>
      <span aria-hidden="true">{directionGlyph(direction)} </span>
      {directionLabel(direction)}
    </span>
  );
}

/**
 * League movement board for one promotion/relegation event.
 *
 * Presentation-only over persisted backend facts: every tile renders the
 * authoritative source league, destination league and movement type. Nothing
 * is resimulated and animation state lives in React only.
 *
 * A completed event opens fully revealed so revisits land on the stable
 * final state immediately; "Replay the reveal" restarts the progressive
 * board, which advances one movement at a time (never on long timers).
 * Promoted athletes arrive upward into the higher league, relegated athletes
 * arrive downward into the lower league; labels and direction always carry
 * the result, never colour alone.
 */
export function MovementReveal({
  boundaries,
  revealKey,
  title,
  meta,
  saveId,
}: {
  /** Boundaries in reveal order (see `buildMovementBoundaries`). */
  boundaries: readonly MovementBoundary[];
  /** Stable identity for the event (e.g. seasons + movement count). */
  revealKey: string;
  /** Human label such as "Season 2 → Season 3". */
  title: string;
  /** Optional meta line such as counts and provenance. */
  meta?: string;
  /** Save context for athlete profile links; when absent names render as text. */
  saveId?: string | null;
}) {
  const order = buildRevealOrder(boundaries);
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

  if (total === 0) {
    return (
      <Card eyebrow="Promotion & relegation" title={title}>
        <Notice tone="empty" title="No league movements">
          <p>This transition holds no promoted or relegated athletes.</p>
        </Notice>
      </Card>
    );
  }

  const revealed = new Set(order.slice(0, revealedCount).map((step) => step.key));
  const isComplete = revealedCount >= total;
  const latest: MovementStep | null = revealedCount > 0 ? (order[revealedCount - 1] ?? null) : null;
  // The active boundary holds the next movement to reveal (or the latest one
  // once complete), so multi-boundary events keep their place obvious.
  const activeKey = latest?.key
    ? (boundaries.find((boundary) => boundary.steps.some((step) => step.key === latest.key))?.key ?? null)
    : (boundaries.find((boundary) => boundary.steps.some((step) => !revealed.has(step.key)))?.key ??
      boundaries[0]?.key ??
      null);

  function revealNext(): void {
    setRevealedCount((value) => Math.min(value + 1, total));
  }

  function revealBoundary(): void {
    if (!activeKey) {
      return;
    }
    const active = boundaries.find((boundary) => boundary.key === activeKey);
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
      eyebrow="Promotion & relegation"
      title={`${title} — ${total} movement${total === 1 ? '' : 's'}`}
      info={
        <div>
          <p>
            Each boundary pairs one feeder league (below) with the Superleague (above). A relegated
            athlete starts in the higher league and moves downward across the boundary; a promoted
            athlete starts in the lower league and moves upward. Tiles land in their destination
            league and stay there.
          </p>
          <p>
            The board replays persisted movement facts only and never resimulates. Reduced-motion
            preferences are respected: tiles appear without sliding and every result keeps its text
            label and direction.
          </p>
        </div>
      }
    >
      {meta ? <p className="muted small">{meta}</p> : null}
      {prefersReducedMotion ? (
        <p className="muted small" role="note">
          Reduced-motion preference detected: tiles appear without sliding. Direction glyphs,
          PROMOTED/RELEGATED labels and league names carry every result.
        </p>
      ) : null}

      <div className="movement-controls" role="group" aria-label="Reveal controls">
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
              <button type="button" className="ghost-button" onClick={revealBoundary}>
                Reveal boundary
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
        aria-label="Movement reveal progress"
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
          The board is face-down. Step with Reveal next — relegated athletes move down first within
          each boundary, promoted athletes move up after.
        </p>
      )}

      <div className="movement-boundaries">
        {boundaries.map((boundary) => {
          const boundaryRevealed = boundary.steps.filter((step) => revealed.has(step.key)).length;
          const isActive = boundary.key === activeKey;
          const upperSteps = boundary.steps.filter(
            (step) => step.direction === 'promoted' && revealed.has(step.key),
          );
          const upperPending = boundary.steps.filter(
            (step) => step.direction === 'promoted' && !revealed.has(step.key),
          );
          const lowerSteps = boundary.steps.filter(
            (step) => step.direction === 'relegated' && revealed.has(step.key),
          );
          const lowerPending = boundary.steps.filter(
            (step) => step.direction === 'relegated' && !revealed.has(step.key),
          );
          return (
            <section
              key={boundary.key}
              id={`movement-boundary-${boundary.key}`}
              className={isActive ? 'movement-boundary is-active' : 'movement-boundary'}
              aria-current={isActive ? 'step' : undefined}
              aria-label={`${boundary.feederLeagueName} and ${boundary.superleagueName} boundary`}
            >
              <header className="movement-boundary-head">
                <h3 className="movement-boundary-title">
                  {boundary.feederLeagueName} ⇄ {boundary.superleagueName}
                </h3>
                <p className="muted small">
                  {boundary.relegated.length} relegated · {boundary.promoted.length} promoted ·{' '}
                  {boundaryRevealed}/{boundary.total} revealed
                  {isActive ? ' · active boundary' : ''}
                </p>
              </header>

              <div className="movement-board">
                <div className="movement-zone movement-zone-upper">
                  <p className="movement-zone-label">
                    {boundary.superleagueName} · higher league
                  </p>
                  <ol className="movement-tiles" aria-label={`${boundary.superleagueName} arrivals`}>
                    {upperSteps.map((step) => (
                      <MovementTile
                        key={step.key}
                        step={step}
                        sequence={order.findIndex((entry) => entry.key === step.key) + 1}
                        total={total}
                        isLatest={latest?.key === step.key}
                        saveId={saveId}
                      />
                    ))}
                    {upperPending.map((step) => (
                      <PendingTile
                        key={step.key}
                        step={step}
                        sequence={order.findIndex((entry) => entry.key === step.key) + 1}
                      />
                    ))}
                    {upperSteps.length === 0 && upperPending.length === 0 ? (
                      <li className="movement-tile is-quiet">
                        <span className="muted small">No arrivals from this boundary.</span>
                      </li>
                    ) : null}
                  </ol>
                </div>

                <div className="movement-divider" aria-hidden="true">
                  <span className="movement-divider-line" />
                  <span className="movement-divider-label">League boundary</span>
                  <span className="movement-divider-line" />
                </div>

                <div className="movement-zone movement-zone-lower">
                  <p className="movement-zone-label">
                    {boundary.feederLeagueName} · lower league
                  </p>
                  <ol className="movement-tiles" aria-label={`${boundary.feederLeagueName} arrivals`}>
                    {lowerSteps.map((step) => (
                      <MovementTile
                        key={step.key}
                        step={step}
                        sequence={order.findIndex((entry) => entry.key === step.key) + 1}
                        total={total}
                        isLatest={latest?.key === step.key}
                        saveId={saveId}
                      />
                    ))}
                    {lowerPending.map((step) => (
                      <PendingTile
                        key={step.key}
                        step={step}
                        sequence={order.findIndex((entry) => entry.key === step.key) + 1}
                      />
                    ))}
                    {lowerSteps.length === 0 && lowerPending.length === 0 ? (
                      <li className="movement-tile is-quiet">
                        <span className="muted small">No arrivals from this boundary.</span>
                      </li>
                    ) : null}
                  </ol>
                </div>
              </div>
            </section>
          );
        })}
      </div>

      {isComplete ? (
        <>
          <h3 className="reveal-subhead">Final summary — all league changes</h3>
          <div className="table-wrap">
            <table className="data-table movement-summary">
              <thead>
                <tr>
                  <th scope="col">Boundary</th>
                  <th scope="col">Movement</th>
                  <th scope="col">Card</th>
                  <th scope="col">Old league → new league</th>
                </tr>
              </thead>
              <tbody>
                {order.map((step) => {
                  const boundary = boundaries.find((entry) =>
                    entry.steps.some((candidate) => candidate.key === step.key),
                  );
                  return (
                    <tr key={step.key}>
                      <td>{boundary?.feederLeagueName ?? '—'}</td>
                      <td>
                        <MovementBadge direction={step.direction} />
                      </td>
                      <td>
                        {saveId ? (
                          <AthleteLink saveId={saveId} athleteId={step.athleteId} name={step.name} />
                        ) : (
                          <span className="card-name">{step.name}</span>
                        )}
                        <span className="card-sub"> · P{step.fromSeasonRank} last season</span>
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
        </>
      ) : null}
    </Card>
  );
}

function MovementTile({
  step,
  sequence,
  total,
  isLatest,
  saveId,
}: {
  step: MovementStep;
  sequence: number;
  total: number;
  isLatest: boolean;
  saveId?: string | null;
}) {
  const arriveClass = step.direction === 'promoted' ? 'arrive-up' : 'arrive-down';
  return (
    <li
      className={
        `movement-tile is-${step.direction}` +
        (isLatest ? ` is-latest ${arriveClass}` : '')
      }
      data-athlete-id={step.athleteId}
      data-revealed="true"
      aria-label={`Move ${sequence} of ${total}: ${stepDescription(step)}`}
    >
      <div className="movement-tile-top">
        <span className="movement-seq">#{sequence}</span>
        <MovementBadge direction={step.direction} />
      </div>
      <div className="movement-tile-art">
        <TileArt step={step} />
      </div>
      <div className="movement-tile-body">
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
          {step.sportingColor} · P{step.fromSeasonRank} last season
        </span>
        <span className="movement-leagues">
          {step.fromLeagueName} → {step.toLeagueName}
        </span>
      </div>
    </li>
  );
}

function PendingTile({ step, sequence }: { step: MovementStep; sequence: number }) {
  return (
    <li
      className="movement-tile is-pending"
      data-revealed="false"
      aria-label={`Move ${sequence} not revealed yet`}
    >
      <div className="movement-tile-top">
        <span className="movement-seq">#{sequence}</span>
        <span className="movement-pending-badge">Face down</span>
      </div>
      <div className="movement-tile-art">
        <span className="movement-card-back" aria-hidden="true">
          <span className="movement-card-back-motif">{step.direction === 'promoted' ? '▲' : '▼'}</span>
          <span className="movement-card-back-text">Face down</span>
        </span>
      </div>
      <div className="movement-tile-body">
        <span className="muted small">Not revealed yet</span>
      </div>
    </li>
  );
}

export function MovementRevealLoading({ title }: { title: string }) {
  return (
    <Card eyebrow="Promotion & relegation" title={title}>
      <Loading label="Loading league movements…" />
    </Card>
  );
}
