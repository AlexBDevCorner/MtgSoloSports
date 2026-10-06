import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import { groupLeaguesByTier } from '../../shared/leagueTiers';
import { AthleteLink, Link } from '../routing/router';
import { cupsPath, livePath, recordsPath, savesPath, standingsLeaguePath, standingsPath } from '../routing/routes';
import type { DashboardData } from './useDashboard';
import { FastForwardSeason } from './FastForwardSeason';
import { FeederLeaders } from './FeederLeaders';
import { SeasonFlow } from './SeasonFlow';
import './DashboardPage.css';

function shortChecksum(value: string): string {
  return value.length > 12 ? `${value.slice(0, 12)}…` : value;
}

function formatPoints(thousandths: number): string {
  return (thousandths / 1000).toFixed(3);
}

function formatBonus(thousandths: number): string {
  const sign = thousandths >= 0 ? '+' : '';
  return `${sign}${(thousandths / 1000).toFixed(3)}%`;
}

export function DashboardPage({
  saveId,
  data,
  loading,
  error,
  notFound,
  onRefresh,
}: {
  saveId: string;
  data: DashboardData | null;
  loading: boolean;
  error: string | null;
  notFound: boolean;
  onRefresh: () => void;
}) {
  if (loading && !data) {
    return <Loading label="Loading dashboard…" />;
  }

  if (notFound || (error && !data)) {
    return (
      <Notice tone="error" title="Save unavailable">
        <p>{error ?? 'That save no longer exists.'}</p>
        <p>
          <Link to={savesPath()} className="primary-button">
            Back to saves
          </Link>
        </p>
      </Notice>
    );
  }

  if (!data) {
    return <Loading label="Loading dashboard…" />;
  }

  const {
    detail,
    progress,
    rosters,
    status,
    stories,
    leaders,
    superleagueComposition,
    recentHonours,
    records,
    hallOfFame,
  } = data;
  const completedTotal = progress.leagues.reduce((sum, league) => sum + league.completedStages, 0);
  const superleagueBoard = leaders.find((board) => board.leagueKind === 'Superleague') ?? null;
  const tierGroups = groupLeaguesByTier(
    progress.leagues.map((league) => ({
      leagueId: league.leagueId,
      name: league.leagueName,
      kind: league.leagueKind,
      feederDivision: league.feederDivision ?? null,
      leagueLevel: league.leagueLevel ?? null,
    })),
  );
  const tierSections = (
    [
      ['Superleague', tierGroups.superleague],
      ['Feeder 1', tierGroups.feeder1],
      ['Feeder 2', tierGroups.feeder2],
      ['Feeder 3', tierGroups.feeder3],
      ['Feeder', tierGroups.legacyFeeder],
    ] as const
  ).filter(([, rows]) => rows.length > 0);
  const leagueById = new Map(progress.leagues.map((league) => [league.leagueId, league]));
  const onTrackCount = progress.leagues.filter(
    (league) => league.isLeagueComplete || league.currentStage === progress.globalStage,
  ).length;
  const cupHonours = recentHonours.filter((honour) => honour.honourKind.includes('Cup'));
  const leagueHonours = recentHonours.filter((honour) => !honour.honourKind.includes('Cup'));
  const recordPreview = (records?.records ?? []).slice(0, 6);

  return (
    <div className="dashboard">
      <dl className="kpis" aria-label="Season summary">
        <div className="kpi">
          <dt>Season</dt>
          <dd>{detail.currentSeason}</dd>
        </div>
        <div className="kpi">
          <dt>Stage</dt>
          <dd>{progress.isSeasonComplete ? 'Complete' : `${progress.globalStage} / 32`}</dd>
        </div>
        <div className="kpi">
          <dt>Phase</dt>
          <dd title={detail.phase}>{detail.phase}</dd>
        </div>
        <div className="kpi">
          <dt>Leagues</dt>
          <dd>{progress.leagues.length}</dd>
        </div>
        <div className="kpi">
          <dt>League-stages</dt>
          <dd>
            {completedTotal} / {progress.leagues.length * 32}
          </dd>
        </div>
        <div className="kpi">
          <dt>Active athletes</dt>
          <dd>{rosters ? rosters.activeAthletes : '—'}</dd>
        </div>
        <div className="kpi">
          <dt>Common pool</dt>
          <dd>{rosters ? rosters.poolAthletes : '—'}</dd>
        </div>
      </dl>

      <Card
        eyebrow={`Season ${status?.sourceSeasonNumber ?? progress.seasonNumber}`}
        title="Next step"
        className="next-action"
        action={
          <button type="button" className="ghost-button" onClick={onRefresh}>
            Refresh
          </button>
        }
        info={
          <p>
            Simulation itself runs on the backend; this board only replays persisted results
            and never resimulates.
          </p>
        }
      >
        <SeasonFlow saveId={saveId} progress={progress} status={status} onAdvanced={onRefresh} />
        {progress.isSeasonComplete ? null : (
          <FastForwardSeason
            saveId={saveId}
            progress={progress}
            status={status}
            onCompleted={onRefresh}
          />
        )}
        <div className="next-action-details">
          {status ? (
            <details className="advanced">
              <summary>Status details</summary>
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
            </details>
          ) : null}
          <details className="advanced">
            <summary>Roster details</summary>
            {rosters ? (
              <>
                <dl className="stats">
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
                      <th scope="col" className="numeric">Count</th>
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
              <p className="muted small">
                Season 1 draw details are only available for saves that completed the inaugural
                draw.
              </p>
            )}
          </details>
        </div>
      </Card>

      <div className="page-grid">
        <Card
          eyebrow="Leaders"
          title={superleagueBoard ? `Superleague — top ${Math.min(5, superleagueBoard.top.length)}` : 'Superleague leaders'}
          action={
            <Link to={livePath(saveId)} className="ghost-button">
              Open live
            </Link>
          }
          info={
            <p>
              Superleague has no color quotas. Composition is informational only; zones are
              1–16 safe, 17–24 qualifier, 25–32 relegated. Full zone badges live on the Live
              tab.
            </p>
          }
        >
          {!superleagueBoard ? (
            <p className="muted">
              Season 1 has feeder leagues only. The inaugural Superleague forms after Season 1;
              from Season 2 its leaders appear here.
            </p>
          ) : superleagueBoard.top.length === 0 ? (
            <p className="muted">No completed stages yet; leaders appear after stage results persist.</p>
          ) : (
            <div className="table-wrap">
              <table className="data-table">
                <thead>
                  <tr>
                    <th scope="col" className="numeric">Rank</th>
                    <th scope="col">Card</th>
                    <th scope="col">Color</th>
                    <th scope="col" className="numeric">Champ pts</th>
                  </tr>
                </thead>
                <tbody>
                  {superleagueBoard.top.map((row) => (
                    <tr key={row.athleteId}>
                      <td className="numeric">{row.seasonRank}</td>
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
                            <AthleteLink saveId={saveId} athleteId={row.athleteId} name={row.name} />
                          </span>
                        </div>
                      </td>
                      <td>{row.sportingColorName}</td>
                      <td className="numeric">{formatPoints(row.totalChampionshipPointsThousandths)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          {superleagueComposition.length > 0 ? (
            <>
              <h3 className="reveal-subhead">Color composition — no quotas applied</h3>
              <ul className="color-counts">
                {superleagueComposition.map((entry) => (
                  <li key={entry.color}>
                    <span>{entry.color}</span>
                    <strong>{entry.count}/32</strong>
                  </li>
                ))}
              </ul>
            </>
          ) : null}
        </Card>

        <FeederLeaders saveId={saveId} leagues={progress.leagues} />

        <Card
          eyebrow="Leagues"
          title={`Stage gate — Season ${progress.seasonNumber}`}
          action={
            <Link to={standingsPath(saveId)} className="ghost-button">
              Standings
            </Link>
          }
          info={
            <p>
              Superleague zones are 1–16 safe, 17–24 qualifier and 25–32 relegated. Feeder zones
              are champion auto-promoted plus 2–4 qualifier. Zones never apply color quotas.
            </p>
          }
        >
          {error ? (
            <Notice tone="warn" title="Showing last loaded state">
              <p>{error}</p>
            </Notice>
          ) : null}
          <p className="muted small">
            {progress.isSeasonComplete
              ? `Season complete — ${progress.leagues.length} leagues × 32 stages persisted.`
              : `Stage ${progress.globalStage}/32 · ${onTrackCount}/${progress.leagues.length} leagues on the global stage · ${completedTotal}/${progress.leagues.length * 32} league-stages persisted.`}
          </p>
          {tierSections.map(([label, rows]) => {
            const tierCompleted = rows.reduce(
              (sum, row) => sum + (leagueById.get(row.leagueId)?.completedStages ?? 0),
              0,
            );
            return (
              <section key={label} aria-label={`${label} stage progress`}>
                <h3 className="reveal-subhead">
                  {label} — {rows.length} league{rows.length === 1 ? '' : 's'} · {tierCompleted}/{rows.length * 32} league-stages
                </h3>
                <div className="table-wrap">
                  <table className="data-table">
                    <thead>
                      <tr>
                        <th scope="col">League</th>
                        <th scope="col" className="numeric">Current stage</th>
                        <th scope="col" className="numeric">Completed</th>
                        <th scope="col">Status</th>
                      </tr>
                    </thead>
                    <tbody>
                      {rows.map((row) => {
                        const league = leagueById.get(row.leagueId);
                        if (!league) {
                          return null;
                        }
                        return (
                          <tr key={league.leagueId}>
                            <td>
                              <Link to={standingsLeaguePath(saveId, league.leagueId)}>{league.leagueName}</Link>
                            </td>
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
                        );
                      })}
                    </tbody>
                  </table>
                </div>
              </section>
            );
          })}
        </Card>

        <Card
          eyebrow="Podiums"
          title={`Recent honours — ${leagueHonours.length} shown`}
          action={
            <Link to={recordsPath(saveId)} className="ghost-button">
              All honours
            </Link>
          }
        >
          {leagueHonours.length === 0 ? (
            <p className="muted">No league honours yet. Final tables persist at season end.</p>
          ) : (
            <div className="table-wrap">
              <table className="data-table">
                <thead>
                  <tr>
                    <th scope="col" className="numeric">Season</th>
                    <th scope="col">League</th>
                    <th scope="col">Athlete</th>
                    <th scope="col">Honour</th>
                  </tr>
                </thead>
                <tbody>
                  {leagueHonours.map((honour, index) => (
                    <tr key={`${honour.seasonNumber}-${honour.leagueName}-${honour.athleteId}-${honour.honourKind}-${index}`}>
                      <td className="numeric">{honour.seasonNumber}</td>
                      <td>{honour.leagueName}</td>
                      <td>
                        <AthleteLink saveId={saveId} athleteId={honour.athleteId} name={honour.athleteName} />
                      </td>
                      <td>{honour.honourKind}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </Card>

        <Card
          eyebrow="Cup context"
          title={status ? `${status.expectedCup} — ${status.cupComplete ? 'complete' : 'pending'}` : 'Post-season Cup'}
        >
          {status ? (
            <dl className="stats">
              <div>
                <dt>Expected Cup</dt>
                <dd>{status.expectedCup}</dd>
              </div>
              <div>
                <dt>Source season</dt>
                <dd>{status.sourceSeasonNumber ?? '—'}</dd>
              </div>
              <div>
                <dt>Selection</dt>
                <dd>{status.cupSelectionResolved ? 'Resolved' : 'Pending'}</dd>
              </div>
              <div>
                <dt>Team event</dt>
                <dd>{status.cupTeamResolved ? 'Resolved' : 'Pending'}</dd>
              </div>
            </dl>
          ) : (
            <p className="muted">Cup lifecycle status is unavailable for this save.</p>
          )}
          {cupHonours.length === 0 ? (
            <p className="muted">No Cup honours yet. Odd seasons run Color Cup, even seasons run Type Cup.</p>
          ) : (
            <ul className="story-list">
              {cupHonours.slice(0, 5).map((honour, index) => (
                <li key={`${honour.seasonNumber}-${honour.honourKind}-${honour.athleteId}-${index}`}>
                  <span className="badge badge-ready">{honour.honourKind}</span>{' '}
                  <AthleteLink saveId={saveId} athleteId={honour.athleteId} name={honour.athleteName} />{' '}
                  <span className="muted small">
                    · Season {honour.seasonNumber} · {honour.leagueName}
                  </span>
                </li>
              ))}
            </ul>
          )}
          <p>
            <Link to={cupsPath(saveId)} className="ghost-button">
              Inspect Cup field
            </Link>
          </p>
        </Card>

        <Card
          eyebrow="Records"
          title={`Meaningful records — ${recordPreview.length} shown`}
          action={
            <Link to={recordsPath(saveId)} className="ghost-button">
              All records
            </Link>
          }
        >
          {recordPreview.length === 0 ? (
            <p className="muted">No records yet. Complete seasons to set career benchmarks.</p>
          ) : (
            <div className="table-wrap">
              <table className="data-table">
                <thead>
                  <tr>
                    <th scope="col">Record</th>
                    <th scope="col" className="numeric">Value</th>
                    <th scope="col">Holders</th>
                  </tr>
                </thead>
                <tbody>
                  {recordPreview.map((record) => (
                    <tr key={record.recordKey}>
                      <td>{record.label}</td>
                      <td className="numeric">{record.valueDisplay}</td>
                      <td>
                        {record.isVacant ? (
                          <span className="muted">Vacant</span>
                        ) : (
                          record.holders.slice(0, 3).map((holder, index) => (
                            <span key={holder.athleteId}>
                              {index > 0 ? ', ' : ''}
                              <AthleteLink saveId={saveId} athleteId={holder.athleteId} name={holder.athleteName} />
                            </span>
                          ))
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </Card>

        <Card eyebrow="Hall of Fame" title={`Career leaders — ${hallOfFame.length} shown`}>
          {hallOfFame.length === 0 ? (
            <p className="muted">No leaders yet. Titles, stage wins and tenure build the table.</p>
          ) : (
            <div className="table-wrap">
              <table className="data-table">
                <thead>
                  <tr>
                    <th scope="col" className="numeric">Rank</th>
                    <th scope="col">Card</th>
                    <th scope="col" className="numeric">Titles</th>
                    <th scope="col" className="numeric">Bonus</th>
                  </tr>
                </thead>
                <tbody>
                  {hallOfFame.map((row) => (
                    <tr key={row.athleteId}>
                      <td className="numeric">{row.rank}</td>
                      <td>
                        <AthleteLink saveId={saveId} athleteId={row.athleteId} name={row.athleteName} />
                      </td>
                      <td className="numeric">{row.totalTitles}</td>
                      <td className="numeric">{formatBonus(row.currentEffectiveBonusThousandths)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </Card>

        <Card
          eyebrow="Stories"
          title="Recent sporting stories"
          info={
            <p>
              Stories are structured backend events with deterministic wording; the reveal only
              replays persisted facts.
            </p>
          }
        >
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
                  <AthleteLink saveId={saveId} athleteId={story.athleteId} name={story.athleteName} />{' '}
                  <span>{story.text}</span>
                </li>
              ))}
            </ul>
          )}
        </Card>
      </div>
    </div>
  );
}
