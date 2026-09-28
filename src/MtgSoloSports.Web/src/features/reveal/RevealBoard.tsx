import { cardCaption, formatMovement, formatPoints } from './format';
import { describeTile, tileMovementGlyph } from './revealBoard';
import type { ProgressiveStandingRow } from './revealOrder';
import './RevealBoard.css';

export interface RevealBoardProps {
  standings: readonly ProgressiveStandingRow[];
  latestAthleteId: number | null;
  onSelectAthlete?: (athleteId: number) => void;
}

function movementClass(rankDelta: number): string {
  if (rankDelta > 0) {
    return 'move-up';
  }
  if (rankDelta < 0) {
    return 'move-down';
  }
  return 'move-flat';
}

function TileName({
  row,
  onSelectAthlete,
}: {
  row: ProgressiveStandingRow;
  onSelectAthlete?: (athleteId: number) => void;
}) {
  if (onSelectAthlete) {
    return (
      <button
        type="button"
        className="card-name card-link reveal-tile-name"
        title={`Open career profile for ${row.name}`}
        onClick={() => {
          onSelectAthlete(row.athleteId);
        }}
      >
        {row.name}
      </button>
    );
  }
  return <span className="card-name reveal-tile-name">{row.name}</span>;
}

/**
 * Visual 32-card board: the primary standings/reveal presentation.
 *
 * Ordered by the current progressive stage standings (rank 1 onward) with a
 * stable React identity per athlete. Pending tiles stay face-down and never
 * render artwork or current-round awards; revealed tiles show the full
 * portrait plus awarded/stage-score/move from the existing progressive row.
 */
export function RevealBoard({ standings, latestAthleteId, onSelectAthlete }: RevealBoardProps) {
  return (
    <ol className="reveal-board" aria-label="Stage standings as revealed">
      {standings.map((row) => {
        const tile = describeTile(row);
        const caption = cardCaption(row.setCode, row.typeLine);
        const isLatest = latestAthleteId !== null && latestAthleteId === row.athleteId;
        const tileClass = `reveal-tile${tile.isRevealed ? '' : ' is-pending'}${isLatest ? ' is-latest' : ''}`;
        return (
          <li
            key={row.athleteId}
            className={tileClass}
            aria-label={tile.accessibleName}
            data-athlete-id={row.athleteId}
            data-revealed={tile.isRevealed ? 'true' : 'false'}
          >
            <div className="reveal-tile-top">
              <span className="reveal-rank" aria-hidden={false}>
                {tile.rankLabel}
              </span>
              {isLatest ? (
                <span className="reveal-latest-badge">Latest</span>
              ) : tile.isRevealed ? null : (
                <span className="reveal-pending-badge">Face down</span>
              )}
            </div>
            <div className="reveal-tile-art">
              {tile.showArtwork && row.imageUrl ? (
                <img
                  className="reveal-card-portrait"
                  src={row.imageUrl}
                  alt=""
                  loading="lazy"
                  draggable={false}
                />
              ) : tile.isRevealed ? (
                <span
                  className="reveal-card-portrait reveal-portrait-fallback"
                  aria-hidden="true"
                >
                  {row.name.slice(0, 2).toUpperCase()}
                </span>
              ) : (
                <span className="reveal-card-back" aria-hidden="true">
                  <span className="reveal-card-back-motif">✦</span>
                  <span className="reveal-card-back-text">Face down</span>
                </span>
              )}
            </div>
            <div className="reveal-tile-body">
              <div className="reveal-tile-identity">
                <TileName row={row} onSelectAthlete={onSelectAthlete} />
                {caption ? <span className="card-sub reveal-tile-caption">{caption}</span> : null}
              </div>
              <dl className="reveal-tile-metrics">
                <div className="reveal-metric">
                  <dt>Awarded</dt>
                  <dd className="numeric">
                    {tile.isRevealed ? `+${formatPoints(row.awardedThousandths)}` : '—'}
                  </dd>
                </div>
                <div className="reveal-metric">
                  <dt>Stage score</dt>
                  <dd className="numeric">{formatPoints(row.displayedScoreThousandths)}</dd>
                </div>
                <div className="reveal-metric">
                  <dt>Move</dt>
                  <dd className="numeric">
                    <span className={movementClass(row.rankDelta)}>
                      <span aria-hidden="true">{tileMovementGlyph(row.rankDelta)} </span>
                      {formatMovement(row.rankDelta)}
                    </span>
                    {tile.isRevealed ? null : (
                      <span className="card-sub"> · waiting</span>
                    )}
                  </dd>
                </div>
              </dl>
            </div>
          </li>
        );
      })}
    </ol>
  );
}
