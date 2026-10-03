import type { ReactNode } from 'react';
import { AthleteLink } from '../routing/router';
import { initials } from './cupFormat';
import './CupHistory.css';

export interface SquadTileMember {
  athleteId: number;
  name: string;
  imageUrl: string | null;
  selectionRank: number;
}

/** Card art, or a lettered stand-in when the athlete has none. Decorative: the name is always shown beside it. */
export function CardArt({ imageUrl, name, small }: { imageUrl: string | null; name: string; small?: boolean }) {
  const size = small ? 'card-art card-art-small' : 'card-art';
  return imageUrl ? (
    <img className={size} src={imageUrl} alt="" loading="lazy" />
  ) : (
    <span className={`${size} card-art-fallback`} aria-hidden="true">
      {initials(name)}
    </span>
  );
}

/** One squad as card tiles in squad-number order; `renderDetail` adds per-member lines. */
export function SquadTiles<T extends SquadTileMember>({
  saveId,
  members,
  renderDetail,
}: {
  saveId: string;
  members: T[];
  renderDetail?: (member: T) => ReactNode;
}) {
  return (
    <ol className="squad-tiles">
      {members.map((member) => (
        <li key={member.athleteId} className="squad-tile">
          <CardArt imageUrl={member.imageUrl} name={member.name} />
          <div className="squad-tile-body">
            <span className="squad-tile-number">#{member.selectionRank}</span>
            <AthleteLink saveId={saveId} athleteId={member.athleteId} name={member.name} />
            {renderDetail ? <div className="squad-tile-detail">{renderDetail(member)}</div> : null}
          </div>
        </li>
      ))}
    </ol>
  );
}
