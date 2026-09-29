import { useEffect, useState } from 'react';
import { cardCaption, formatMovement, formatPoints } from './format';
import { describeTile, tileMovementGlyph } from './revealBoardHelpers';
import type { ProgressiveStandingRow } from './revealOrder';
import { AthleteLink } from '../routing/router';
import './RevealBoard.css';

export interface RevealBoardProps {
  standings: readonly ProgressiveStandingRow[];
  latestAthleteId: number | null;
  /** Save context for profile links; when absent the name renders as text. */
  saveId?: string | null;
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

function TileName({ row, saveId }: { row: ProgressiveStandingRow; saveId?: string | null }) {
  if (saveId) {
    return (
      <AthleteLink saveId={saveId} athleteId={row.athleteId} name={row.name} className="card-name card-link reveal-tile-name" />
    );
  }
  return <span className="card-name reveal-tile-name">{row.name}</span>;
}

/**
 * Tile artwork with a visually consistent portrait fallback.
 *
 * Handles both absent URLs (caller renders the fallback directly) and URLs
 * whose image request fails at runtime: a failed load flips to the same
 * initials portrait instead of leaving a broken-image icon. Failure state
 * resets whenever the URL changes so a new URL gets a fresh load attempt.
 */
function TileArt({ row, showArtwork }: { row: ProgressiveStandingRow; showArtwork: boolean }) {
  const [imageFailed, setImageFailed] = useState(false);
  const imageUrl = row.imageUrl;

  useEffect(() => {
    setImageFailed(false);
  }, [imageUrl]);

  if (showArtwork && imageUrl && !imageFailed) {
    return (
      <img
        className="reveal-card-portrait"
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
  if (row.isRevealed) {
    return (
      <span className="reveal-card-portrait reveal-portrait-fallback" aria-hidden="true">
        {row.name.slice(0, 2).toUpperCase()}
      </span>
    );
  }
  return (
    <span className="reveal-card-back" aria-hidden="true">
      <span className="reveal-card-back-motif">✦</span>
      <span className="reveal-card-back-text">Face down</span>
    </span>
  );
}

/**
 * Visual 32-card board: the primary standings/reveal presentation.
 *
 * Ordered by the current progressive stage standings (rank 1 onward) with a
 * stable React identity per athlete. Pending tiles stay face-down and never
 * render artwork or current-round awards; revealed tiles show the full
 * portrait plus awarded/stage-score/move from the existing progressive row.
 */
export function RevealBoard({ standings, latestAthleteId, saveId }: RevealBoardProps) {
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
              <TileArt row={row} showArtwork={tile.showArtwork} />
            </div>
            <div className="reveal-tile-body">
              <div className="reveal-tile-identity">
                <TileName row={row} saveId={saveId} />
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
