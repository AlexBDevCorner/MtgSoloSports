import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import { describeNextAction, type DashboardData } from './useDashboard';

function shortChecksum(value: string): string {
  return value.length > 12 ? `${value.slice(0, 12)}…` : value;
}

export function DashboardPage({
  data,
  loading,
  error,
  notFound,
  hasSelection,
  onRefresh,
  onGoToSaves,
}: {
  data: DashboardData | null;
  loading: boolean;
  error: string | null;
  notFound: boolean;
  hasSelection: boolean;
  onRefresh: () => void;
  onGoToSaves: () => void;
}) {
  if (!hasSelection) {
    return (
      <Notice tone="empty" title="No save selected">
        <p>Pick a universe on the Saves tab to see its current sporting state.</p>
        <p>
          <button type="button" className="primary-button" onClick={onGoToSaves}>
            Go to saves
          </button>
        </p>
      </Notice>
    );
  }

  if (loading && !data) {
    return <Loading label="Loading dashboard…" />;
  }

  if (notFound || (error && !data)) {
    return (
      <Notice tone="error" title="Save unavailable">
        <p>{error ?? 'That save no longer exists.'}</p>
        <p>
          <button type="button" className="primary-button" onClick={onGoToSaves}>
            Back to saves
          </button>
        </p>
      </Notice>
    );
  }

  if (!data) {
    return <Loading label="Loading dashboard…" />;
  }

  const { detail, progress, rosters, status, stories } = data;
  const next = describeNextAction(progress, status);
  const completedTotal = progress.leagues.reduce((sum, league) => sum + league.completedStages, 0);

  return (
    <div className="dashboard">
      <div className="page-grid cards-3">
        <Card eyebrow="Competition" title={`Season ${detail.currentSeason}`}>
          <dl className="stats">
            <div>
              <dt>Phase</dt>
              <dd>{detail.phase}</dd>
            </div>
            <div>
              <dt>Global stage</dt>
              <dd>{progress.isSeasonComplete ? 'Complete (32/32)' : `${progress.globalStage} / 32`}</dd>
            </div>
            <div>
              <dt>Completed league-stages</dt>
              <dd>{completedTotal} / {progress.leagues.length * 32}</dd>
            </div>
            <div>
              <dt>Active leagues</dt>
              <dd>{progress.leagues.length}</dd>
            </div>
          </dl>
          {progress.isSeasonComplete ? (
            <p className="muted">Season final tables are persisted and stable.</p>
          ) : (
            <p className="muted">
              Stage {progress.globalStage} cannot advance until every active league completes
              it.
            </p>
          )}
        </Card>

        <Card eyebrow="Roster" title="League & pool">
          {rosters ? (
            <>
              <dl className="stats">
                <div>
                  <dt>Active athletes</dt>
                  <dd>{rosters.activeAthletes}</dd>
                </div>
                <div>
                  <dt>Common pool</dt>
                  <dd>{rosters.poolAthletes}</dd>
                </div>
                <div>
                  <dt>Draw checksum</dt>
                  <dd>
                    <code title={rosters.drawChecksum}>{shortChecksum(rosters.drawChecksum)}</code>
                  </dd>
                </div>
              </dl>
              <table className="mini-table">
                <thead>
                  <tr>
                    <th scope="col">Color pool</th>
                    <th scope="col">Count</th>
                  </tr>
                </thead>
                <tbody>
                  {rosters.poolCounts.map((pool) => (
                    <tr key={pool.sportingColor}>
                      <td>{pool.sportingColor}</td>
                      <td className="numeric">{pool.count}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </>
          ) : (
            <Notice tone="info" title="Roster snapshot unavailable">
              <p>
                Season 1 draw details are only available for saves that completed the
                inaugural draw. Progress below still reflects live backend state.
              </p>
            </Notice>
          )}
        </Card>

        <Card
          eyebrow="Next action"
          title="Available simulation"
          action={
            <button type="button" className="ghost-button" onClick={onRefresh}>
              Refresh
            </button>
          }
        >
          <p className="next-headline">{next.headline}</p>
          <p className="muted">{next.detail}</p>
          {status ? (
            <dl className="stats">
              <div>
                <dt>Lifecycle phase</dt>
                <dd>{status.computedPhase}</dd>
              </div>
              <div>
                <dt>Legal next action</dt>
                <dd>{status.legalNextActions.join(', ')}</dd>
              </div>
              <div>
                <dt>Expected Cup</dt>
                <dd>{status.expectedCup}</dd>
              </div>
              <div>
                <dt>Cup selection</dt>
                <dd>{status.cupSelectionResolved ? 'Resolved' : 'Pending'}</dd>
              </div>
              <div>
                <dt>Cup individual</dt>
                <dd>
                  {status.expectedCup === 'TypeCup'
                    ? 'N/A (team-only)'
                    : status.cupIndividualResolved
                      ? 'Resolved'
                      : 'Pending'}
                </dd>
              </div>
              <div>
                <dt>Cup team</dt>
                <dd>{status.cupTeamResolved ? 'Resolved' : 'Pending'}</dd>
              </div>
              <div>
                <dt>Cup complete</dt>
                <dd>{status.cupComplete ? 'Complete' : 'Pending'}</dd>
              </div>
            </dl>
          ) : null}
          {status?.sourceSeasonNumber !== null && status?.sourceSeasonNumber !== undefined ? (
            <p className="muted small">
              Post-season {status.expectedCup} for Season {status.sourceSeasonNumber} runs after
              feeder rebalancing and before bonus aging. Each Cup step is an explicit Next
              Event: inspect the field on the Cups tab before running the events.
            </p>
          ) : null}
          <p className="muted small">
            Simulation itself runs on the backend; this board only replays persisted results
            and never resimulates.
          </p>
        </Card>
      </div>

      <Card eyebrow="Leagues" title={`Stage gate — Season ${progress.seasonNumber}`}>
        {error ? (
          <Notice tone="warn" title="Showing last loaded state">
            <p>{error}</p>
          </Notice>
        ) : null}
        <div className="table-wrap">
          <table className="data-table">
            <thead>
              <tr>
                <th scope="col">League</th>
                <th scope="col">Current stage</th>
                <th scope="col">Completed</th>
                <th scope="col">Status</th>
              </tr>
            </thead>
            <tbody>
              {[...progress.leagues]
                .sort((a, b) => a.leagueId - b.leagueId)
                .map((league) => (
                  <tr key={league.leagueId}>
                    <td>{league.leagueName}</td>
                    <td className="numeric">{league.currentStage ?? '—'}</td>
                    <td className="numeric">{league.completedStages} / 32</td>
                    <td>
                      {league.isLeagueComplete ? (
                        <span className="badge badge-done">Complete</span>
                      ) : league.currentStage === progress.globalStage ? (
                        <span className="badge badge-ready">Ready</span>
                      ) : (
                        <span className="badge badge-wait">Waiting</span>
                      )}
                    </td>
                  </tr>
                ))}
            </tbody>
          </table>
        </div>
      </Card>

      <Card eyebrow="Stories" title="Recent sporting stories">
        {stories.length === 0 ? (
          <p className="muted">
            No stories yet. Stage wins, titles, promotions and pool returns appear here
            once the backend simulates them.
          </p>
        ) : (
          <ul className="story-list">
            {stories.map((story) => (
              <li key={story.id}>
                <span className="badge badge-ready">{story.eventType}</span>{' '}
                <span>{story.text}</span>
              </li>
            ))}
          </ul>
        )}
        <p className="muted small">
          Stories are structured backend events with deterministic wording; the reveal
          only replays persisted facts.
        </p>
      </Card>
    </div>
  );
}
