import { Card } from '../../shared/ui/Card';
import { Notice } from '../../shared/ui/Notice';
import { Link } from '../routing/router';
import { dashboardPath } from '../routing/routes';
import './LivePage.css';

/**
 * MSS-143 bare-Live fallback when no playable live event exists and the next
 * legal lifecycle step must run on the Dashboard (setup/selection actions,
 * movement/rebalance pre-resolution, season start, unknown steps).
 *
 * Never shows stale Superleague standings/content as the active competition;
 * the season, phase and next step are labelled explicitly so the user knows
 * Live is waiting on a Dashboard action, not presenting past results as live.
 */
export function LiveDashboardCta({
  saveId,
  season,
  action,
  headline,
  explanation,
}: {
  saveId: string;
  season: number | null;
  action: string | null;
  headline: string | null;
  explanation: string | null;
}) {
  const seasonLabel = season !== null ? `Season ${season}` : 'Current season';
  const title = headline ?? (action ? `Next: ${action}` : 'No playable live event');
  return (
    <div className="live-layout">
      <aside className="live-sidebar" aria-label="Live status">
        <Card eyebrow={`Live • ${seasonLabel}`} title={title}>
          <p className="muted small">
            {explanation ?? 'The next season step must be performed on the Dashboard before Live has a playable event.'}
          </p>
          <p className="live-buttons">
            <Link to={dashboardPath(saveId)} className="primary-button">
              Continue on the Dashboard
            </Link>
          </p>
          {action ? <p className="muted small">Next step: {action}.</p> : null}
        </Card>
      </aside>
      <div className="live-main">
        <Notice tone="info" title={title}>
          <p>
            {seasonLabel}
            {action ? ` • ${action}` : ''} — there is no playable live event right now. The next legal step
            runs on the Dashboard.
          </p>
          <p className="live-buttons">
            <Link to={dashboardPath(saveId)} className="primary-button">
              Continue on the Dashboard
            </Link>
          </p>
        </Notice>
      </div>
    </div>
  );
}
