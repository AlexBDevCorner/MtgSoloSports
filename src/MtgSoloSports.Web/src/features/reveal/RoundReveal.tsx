import { Card } from '../../shared/ui/Card';
import { cardCaption, formatBonus, formatMovement, formatPoints } from './format';
import type { RevealPlacement } from './types';
import { REVEAL_SPEEDS } from './types';
import { useRoundReveal } from './useRoundReveal';

export interface RoundRevealProps {
  /** Immutable persisted placements for one round (live or historical). */
  placements: readonly RevealPlacement[];
  /** Stable identity for the round (e.g. checksum + round number). */
  revealKey: string;
  /** Human label such as "Round 3". */
  roundLabel: string;
  /** Optional meta line such as rules version + checksum note. */
  meta?: string;
  onSelectAthlete?: (athleteId: number) => void;
}

function speedLabel(speed: (typeof REVEAL_SPEEDS)[number]): string {
  switch (speed) {
    case 'slow':
      return 'Slow';
    case 'normal':
      return 'Normal';
    case 'fast':
      return 'Fast';
    case 'turbo':
      return 'Turbo';
  }
}

/**
 * Eurovision-style animated reveal over one immutable backend round result.
 *
 * Presentation-only: every number renders verbatim persisted thousandths,
 * animation state lives in React (`useRoundReveal`), and no callback mutates
 * sporting state or calls simulation endpoints. The same component backs a
 * live completed round and a historical replay.
 */
export function RoundReveal({
  placements,
  revealKey,
  roundLabel,
  meta,
  onSelectAthlete,
}: RoundRevealProps) {
  const reveal = useRoundReveal(placements, revealKey);
  const latestRevealed = reveal.revealedFeed.length > 0
    ? reveal.revealedFeed[reveal.revealedFeed.length - 1]!
    : null;

  // Instant mode lists the full finishing order; animated mode lists cards in
  // Eurovision reveal order (lowest first) so the winner lands last.
  const instantOrder: RevealPlacement[] = [...placements].sort((a, b) => a.position - b.position);
  const feed: RevealPlacement[] = reveal.mode === 'instant' ? instantOrder : reveal.revealedFeed;
  const total = reveal.total;

  function openAthlete(athleteId: number): void {
    onSelectAthlete?.(athleteId);
  }

  return (
    <Card
      eyebrow={reveal.mode === 'instant' ? 'Instant results' : 'Animated reveal'}
      title={`${roundLabel} — ${total} athletes`}
      action={
        <div className="mode-toggle" role="group" aria-label="Results mode">
          <button
            type="button"
            className={reveal.mode === 'instant' ? 'nav-item current' : 'nav-item'}
            aria-pressed={reveal.mode === 'instant'}
            onClick={() => {
              reveal.setMode('instant');
            }}
          >
            Instant
          </button>
          <button
            type="button"
            className={reveal.mode === 'animated' ? 'nav-item current' : 'nav-item'}
            aria-pressed={reveal.mode === 'animated'}
            title={
              reveal.prefersReducedMotion
                ? 'Reduced motion is on: the accessible instant table is used instead.'
                : 'Reveal cards one by one from lowest to winner.'
            }
            onClick={() => {
              reveal.setMode('animated');
            }}
          >
            Animated
          </button>
        </div>
      }
    >
      {meta ? <p className="muted small">{meta}</p> : null}
      {reveal.prefersReducedMotion ? (
        <p className="muted small" role="note">
          Reduced-motion preference detected: animation is off and the full instant table is
          shown. This accessible alternative never resimulates and never consumes RNG.
        </p>
      ) : (
        <p className="muted small">
          Animated reveal replays the same persisted rows from lowest finisher to winner.
          Pausing, changing speed or replaying only re-reads those rows and never
          resimulates.
        </p>
      )}

      {reveal.mode === 'animated' && !reveal.prefersReducedMotion ? (
        <div className="reveal-controls" role="group" aria-label="Reveal playback controls">
          <div className="live-buttons">
            <button
              type="button"
              className="primary-button"
              aria-label={reveal.isPlaying ? 'Pause reveal' : 'Play reveal'}
              disabled={total === 0}
              onClick={() => {
                reveal.togglePlay();
              }}
            >
              {reveal.isPlaying ? 'Pause' : reveal.revealedCount === 0 ? 'Play' : reveal.isComplete ? 'Replay' : 'Resume'}
            </button>
            <button
              type="button"
              className="ghost-button"
              aria-label="Replay from the first card"
              disabled={total === 0}
              onClick={() => {
                reveal.replay();
              }}
            >
              Restart
            </button>
            <button
              type="button"
              className="ghost-button"
              aria-label="Reveal one card fewer"
              disabled={reveal.revealedCount <= 0}
              onClick={() => {
                reveal.stepBack();
              }}
            >
              −1
            </button>
            <button
              type="button"
              className="ghost-button"
              aria-label="Reveal one more card"
              disabled={reveal.revealedCount >= total}
              onClick={() => {
                reveal.stepForward();
              }}
            >
              +1
            </button>
            <button
              type="button"
              className="ghost-button"
              aria-label="Show all cards immediately"
              disabled={reveal.revealedCount >= total}
              onClick={() => {
                reveal.showAll();
              }}
            >
              Show all ({reveal.revealedCount}/{total})
            </button>
          </div>
          <label className="field reveal-speed">
            <span>Speed</span>
            <select
              value={reveal.speed}
              aria-label="Reveal speed"
              onChange={(event) => {
                const next = event.target.value;
                if (next === 'slow' || next === 'normal' || next === 'fast' || next === 'turbo') {
                  reveal.setSpeed(next);
                }
              }}
            >
              {REVEAL_SPEEDS.map((speed) => (
                <option key={speed} value={speed}>
                  {speedLabel(speed)}
                </option>
              ))}
            </select>
          </label>
        </div>
      ) : null}

      <div
        className="reveal-progress"
        role="progressbar"
        aria-label="Reveal progress"
        aria-valuemin={0}
        aria-valuemax={total}
        aria-valuenow={reveal.mode === 'instant' ? total : reveal.revealedCount}
      >
        <div
          className="reveal-progress-bar"
          style={{
            width: total === 0 ? '0%' : `${((reveal.mode === 'instant' ? total : reveal.revealedCount) / total) * 100}%`,
          }}
        />
      </div>
      <p className="muted small" aria-live="polite">
        {reveal.mode === 'instant'
          ? `Showing all ${total} persisted rows at once — presentation only, nothing is recalculated.`
          : `Revealed ${reveal.revealedCount}/${total} persisted rows — presentation only, nothing is recalculated.`}
      </p>

      {reveal.mode === 'animated' && latestRevealed ? (
        <div className="reveal-spotlight" aria-live="polite">
          <div className="card-cell">
            {latestRevealed.imageUrl ? (
              <img className="card-thumb" src={latestRevealed.imageUrl} alt="" loading="lazy" />
            ) : (
              <span className="card-thumb card-thumb-fallback" aria-hidden="true">
                {latestRevealed.name.slice(0, 2).toUpperCase()}
              </span>
            )}
            <span className="card-identity">
              <span className="card-name">
                P{latestRevealed.position} · {latestRevealed.name}
              </span>
              <span className="card-sub">
                +{formatPoints(latestRevealed.finalThousandths)} pts · stage{' '}
                {formatPoints(latestRevealed.cumulativeAfterThousandths)}
              </span>
            </span>
          </div>
        </div>
      ) : null}

      <h3 className="reveal-subhead">Cumulative stage standings as revealed</h3>
      <div className="table-wrap">
        <table className="data-table reveal-standings">
          <thead>
            <tr>
              <th scope="col">Rank</th>
              <th scope="col">Card</th>
              <th scope="col">Awarded</th>
              <th scope="col">Stage score</th>
              <th scope="col">Move</th>
            </tr>
          </thead>
          <tbody>
            {reveal.standings.map((row) => {
              const caption = cardCaption(row.setCode, row.typeLine);
              const rowClass = row.isRevealed ? '' : 'reveal-pending';
              const latestClass =
                reveal.mode === 'animated' && latestRevealed?.athleteId === row.athleteId
                  ? ' reveal-latest'
                  : '';
              return (
                <tr key={row.athleteId} className={`${rowClass}${latestClass}`}>
                  <td className="numeric">{row.currentRank}</td>
                  <td>
                    <div className="card-cell">
                      {row.imageUrl ? (
                        <img className="card-thumb" src={row.imageUrl} alt="" loading="lazy" />
                      ) : (
                        <span className="card-thumb card-thumb-fallback" aria-hidden="true">
                          {row.name.slice(0, 2).toUpperCase()}
                        </span>
                      )}
                      <span className="card-identity">
                        {onSelectAthlete ? (
                          <button
                            type="button"
                            className="card-name card-link"
                            title={`Open career profile for ${row.name}`}
                            onClick={() => {
                              openAthlete(row.athleteId);
                            }}
                          >
                            {row.name}
                          </button>
                        ) : (
                          <span className="card-name">{row.name}</span>
                        )}
                        {caption ? <span className="card-sub">{caption}</span> : null}
                      </span>
                    </div>
                  </td>
                  <td className="numeric">
                    {row.isRevealed ? `+${formatPoints(row.awardedThousandths)}` : '—'}
                  </td>
                  <td className="numeric">{formatPoints(row.displayedScoreThousandths)}</td>
                  <td className="numeric">
                    <span
                      className={
                        row.rankDelta > 0 ? 'move-up' : row.rankDelta < 0 ? 'move-down' : 'move-flat'
                      }
                    >
                      {formatMovement(row.rankDelta)}
                    </span>
                    {row.isRevealed ? null : <span className="card-sub"> · waiting</span>}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>

      <h3 className="reveal-subhead">
        {reveal.mode === 'instant' ? 'Full finishing order' : 'Reveal feed (lowest first)'}
      </h3>
      <div className="table-wrap">
        <table className="data-table">
          <thead>
            <tr>
              <th scope="col">Pos</th>
              <th scope="col">Card</th>
              <th scope="col">Base</th>
              <th scope="col">Bonus</th>
              <th scope="col">Final</th>
              <th scope="col">Rank</th>
              <th scope="col">Stage score</th>
            </tr>
          </thead>
          <tbody>
            {feed.map((placement) => {
              const caption = cardCaption(placement.setCode, placement.typeLine);
              const isLatest =
                reveal.mode === 'animated' && latestRevealed?.athleteId === placement.athleteId;
              return (
                <tr key={placement.athleteId} className={isLatest ? 'reveal-latest' : undefined}>
                  <td className="numeric">{placement.position}</td>
                  <td>
                    <div className="card-cell">
                      {placement.imageUrl ? (
                        <img
                          className="card-thumb"
                          src={placement.imageUrl}
                          alt=""
                          loading="lazy"
                        />
                      ) : (
                        <span className="card-thumb card-thumb-fallback" aria-hidden="true">
                          {placement.name.slice(0, 2).toUpperCase()}
                        </span>
                      )}
                      <span className="card-identity">
                        {onSelectAthlete ? (
                          <button
                            type="button"
                            className="card-name card-link"
                            title={`Open career profile for ${placement.name}`}
                            onClick={() => {
                              openAthlete(placement.athleteId);
                            }}
                          >
                            {placement.name}
                          </button>
                        ) : (
                          <span className="card-name">{placement.name}</span>
                        )}
                        {caption ? <span className="card-sub">{caption}</span> : null}
                      </span>
                    </div>
                  </td>
                  <td className="numeric">{formatPoints(placement.baseThousandths)}</td>
                  <td className="numeric">{formatBonus(placement.activeBonusThousandths)}</td>
                  <td className="numeric">{formatPoints(placement.finalThousandths)}</td>
                  <td className="numeric">
                    {placement.rankBefore} → {placement.rankAfter}{' '}
                    <span
                      className={
                        placement.rankMovement > 0
                          ? 'move-up'
                          : placement.rankMovement < 0
                            ? 'move-down'
                            : 'move-flat'
                      }
                    >
                      ({formatMovement(placement.rankMovement)})
                    </span>
                  </td>
                  <td className="numeric">
                    {formatPoints(placement.cumulativeAfterThousandths)}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </Card>
  );
}
