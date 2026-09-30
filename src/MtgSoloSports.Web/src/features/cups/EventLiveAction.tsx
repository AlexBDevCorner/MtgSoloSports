import type { SeasonStatus } from '../dashboard/dashboardApi';
import { progressLabel, type EventKey } from '../events/eventModel';
import { Link } from '../routing/router';
import { livePath } from '../routing/routes';

/**
 * Cup events are played round by round on Live. Shown while this event is the
 * next lifecycle step, even when the page still shows an older Cup's result:
 * the season it will play, in-progress position (never results) and a link to
 * play it on Live.
 */
export function EventLiveAction({
  saveId,
  eventKey,
  status,
}: {
  saveId: string;
  eventKey: EventKey;
  status: SeasonStatus | null;
}) {
  const progress = status?.eventProgress ?? null;
  if (!progress || progress.event !== eventKey) {
    return null;
  }
  return (
    <>
      <p className="muted">
        Season {progress.sourceSeasonNumber}:{' '}
        {progress.roundsPlayed > 0 ? `In progress — ${progressLabel(progress)}.` : 'ready to play.'}
      </p>
      <p>
        <Link to={livePath(saveId, { event: eventKey, season: progress.sourceSeasonNumber })} className="primary-button">
          Play on Live
        </Link>
      </p>
    </>
  );
}
