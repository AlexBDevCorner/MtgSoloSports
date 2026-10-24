import { useEffect, useMemo, useState } from 'react';
import { apiErrorMessage, ApiError } from '../../shared/api/http';
import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import { leagueLevelLabel } from '../../shared/leagueTiers';
import { AthleteLink, Link } from '../routing/router';
import { historyPath, leaguePath, leaguesPath, standingsLeaguePath } from '../routing/routes';
import { formatPoints } from '../standings/matrix';
import { fetchLeagueProfile, type LeagueProfile } from './leaguesApi';
import { leagueKeyLabel } from './leagueKeys';
import './Leagues.css';

const LEADERBOARD_PREVIEW = 10;

/**
 * Dedicated historical profile for one stable league identity. Podium
 * history, the all-time Total league points leaderboard and records all come
 * from one aggregated backend response built exclusively from completed
 * official final standings in this league. The page never reconstructs
 * history per season and never mixes in Cup, Career Leaders or bonus data.
 */
export function LeagueProfilePage({ saveId, leagueKey }: { saveId: string; leagueKey: string }) {
  const [data, setData] = useState<LeagueProfile | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notFound, setNotFound] = useState(false);
  const [showAll, setShowAll] = useState(false);

  useEffect(() => {
    const controller = new AbortController();
    setError(null);
    setNotFound(false);
    setData(null);
    setShowAll(false);
    fetchLeagueProfile(saveId, leagueKey, controller.signal)
      .then((loaded) => {
        if (loaded.saveId !== saveId) {
          return;
        }
        setData(loaded);
      })
      .catch((failure: unknown) => {
        if (failure instanceof DOMException && failure.name === 'AbortError') {
          return;
        }
        if (failure instanceof ApiError && failure.status === 404) {
          setNotFound(true);
          return;
        }
        setError(apiErrorMessage(failure));
      });
    return () => controller.abort();
  }, [saveId, leagueKey]);

  // Stale-payload guards: only render payloads that match the requested
  // save and league key, so universe switches and Back/Forward never show
  // another universe's or another league's history.
  const profile = data && data.saveId === saveId && data.leagueKey === leagueKey ? data : null;

  const legends = useMemo(() => legendLines(profile), [profile]);
  const visibleBoard = useMemo(
    () =>
      profile && !showAll
        ? profile.allTimeLeaderboard.slice(0, LEADERBOARD_PREVIEW)
        : (profile?.allTimeLeaderboard ?? []),
    [profile, showAll],
  );

  if (notFound) {
    return (
      <Notice tone="error" title="League not found">
        <p>
          “{leagueKey}” has no recorded seasons in this universe. Pick another league below; no other
          league was substituted.
        </p>
        <p>
          <Link to={leaguesPath(saveId)} className="ghost-button">
            All leagues
          </Link>
        </p>
      </Notice>
    );
  }
  if (error) {
    return (
      <Notice tone="error" title="League profile unavailable">
        <p>{error}</p>
      </Notice>
    );
  }
  if (!profile) {
    return <Loading label={`Loading ${leagueKeyLabel(leagueKey)}…`} />;
  }

  const tier = leagueLevelLabel(profile.leagueLevel, profile.feederDivision, profile.kind);
  const latest = profile.podiumHistory.length > 0 ? profile.podiumHistory[0]! : null;

  return (
    <div className="dashboard">
      <Card
        eyebrow="League profile"
        title={`${profile.name} · ${tier}`}
        info={
          <p>
            All panels below read completed official final standings in this league only. All-time
            totals sum each athlete&apos;s final season championship points while competing here;
            weighted Hall of Fame career points, active bonuses and Cup results never contribute.
          </p>
        }
      >
        <dl className="stats">
          <div>
            <dt>Completed seasons</dt>
            <dd>{profile.completedSeasons}</dd>
          </div>
          <div>
            <dt>Historical athletes</dt>
            <dd>{profile.uniqueAthletes}</dd>
          </div>
          <div>
            <dt>Span</dt>
            <dd>
              {profile.firstSeasonNumber !== null ? `Season ${profile.firstSeasonNumber}` : '—'}
              {profile.lastCompletedSeasonNumber !== null &&
              profile.lastCompletedSeasonNumber !== profile.firstSeasonNumber
                ? ` – Season ${profile.lastCompletedSeasonNumber}`
                : ''}
            </dd>
          </div>
          <div>
            <dt>Latest champion</dt>
            <dd>
              {latest?.champion ? (
                <>
                  <AthleteLink
                    saveId={saveId}
                    athleteId={latest.champion.athleteId}
                    name={latest.champion.athleteName}
                  />{' '}
                  <span className="card-sub">
                    · Season {latest.seasonNumber} · {formatPoints(latest.champion.totalChampionshipPointsThousandths)}
                  </span>
                </>
              ) : (
                '—'
              )}
            </dd>
          </div>
        </dl>
        {legends.length > 0 ? (
          <p className="muted">
            <strong>Legends of this league:</strong> {legends.join(' · ')}
          </p>
        ) : null}
        <p className="muted small">
          <Link to={leaguesPath(saveId)}>All leagues</Link>
          {latest ? (
            <>
              {' · '}
              <Link
                to={standingsLeaguePath(saveId, latest.leagueId, { season: latest.seasonNumber })}
              >
                Season {latest.seasonNumber} standings
              </Link>
            </>
          ) : null}
        </p>
      </Card>

      {profile.currentSeason ? (
        <Card eyebrow="In progress" title={`Season ${profile.currentSeason.seasonNumber} — not yet final`}>
          <Notice tone="info" title="Ongoing season">
            <p>
              Season {profile.currentSeason.seasonNumber} is still in progress and does not count as a
              medal, record or all-time point. Historical achievements above stay with the exact
              league and season where they happened.
            </p>
            <p>
              <Link
                to={standingsLeaguePath(saveId, profile.currentSeason.leagueId, {
                  season: profile.currentSeason.seasonNumber,
                })}
                className="ghost-button"
              >
                Open live standings
              </Link>
            </p>
          </Notice>
        </Card>
      ) : null}

      <Card
        eyebrow="Gold · silver · bronze"
        title={`Season-by-season podium history — ${profile.podiumHistory.length} completed`}
        info={<p>Newest first, from finalized tables only. Ties follow official league ranking.</p>}
      >
        {profile.podiumHistory.length === 0 ? (
          <Notice tone="empty" title="No completed seasons">
            <p>This league has no finalized seasons to award medals from.</p>
          </Notice>
        ) : (
          <div className="table-wrap">
            <table className="data-table">
              <thead>
                <tr>
                  <th scope="col" className="numeric">Season</th>
                  <th scope="col">🥇 Champion</th>
                  <th scope="col">🥈 Runner-up</th>
                  <th scope="col">🥉 Third</th>
                  <th scope="col" className="numeric">Margin</th>
                </tr>
              </thead>
              <tbody>
                {profile.podiumHistory.map((season) => (
                  <tr key={season.seasonNumber}>
                    <td className="numeric">
                      <Link
                        to={standingsLeaguePath(saveId, season.leagueId, { season: season.seasonNumber })}
                        title={`Open Season ${season.seasonNumber} standings`}
                      >
                        {season.seasonNumber}
                      </Link>
                    </td>
                    <td>
                      {season.champion ? (
                        <AthleteLink
                          saveId={saveId}
                          athleteId={season.champion.athleteId}
                          name={season.champion.athleteName}
                        />
                      ) : (
                        <span className="muted">—</span>
                      )}
                      {season.champion ? (
                        <span className="card-sub">
                          {' '}
                          · {formatPoints(season.champion.totalChampionshipPointsThousandths)}
                        </span>
                      ) : null}
                    </td>
                    <td>
                      {season.runnerUp ? (
                        <AthleteLink
                          saveId={saveId}
                          athleteId={season.runnerUp.athleteId}
                          name={season.runnerUp.athleteName}
                        />
                      ) : (
                        <span className="muted">—</span>
                      )}
                    </td>
                    <td>
                      {season.third ? (
                        <AthleteLink
                          saveId={saveId}
                          athleteId={season.third.athleteId}
                          name={season.third.athleteName}
                        />
                      ) : (
                        <span className="muted">—</span>
                      )}
                    </td>
                    <td className="numeric">
                      {season.winningMarginThousandths !== null
                        ? formatPoints(season.winningMarginThousandths)
                        : '—'}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>

      <Card
        eyebrow="All time in this league"
        title={`Total league points — ${profile.allTimeLeaderboard.length} athletes`}
        info={
          <p>
            Sum of official final season championship points earned while competing in this league,
            across completed seasons only. Repeat spells count; other leagues and Cups never leak in.
          </p>
        }
      >
        {profile.allTimeLeaderboard.length === 0 ? (
          <Notice tone="empty" title="No all-time points yet">
            <p>Completed seasons will accumulate here.</p>
          </Notice>
        ) : (
          <>
            <div className="table-wrap">
              <table className="data-table">
                <thead>
                  <tr>
                    <th scope="col" className="numeric">Rank</th>
                    <th scope="col">Card</th>
                    <th scope="col" className="numeric">Total league points</th>
                    <th scope="col" className="numeric">Seasons</th>
                    <th scope="col" className="numeric">Best</th>
                    <th scope="col" className="numeric">Titles</th>
                    <th scope="col" className="numeric">Podiums</th>
                  </tr>
                </thead>
                <tbody>
                  {visibleBoard.map((row, index) => (
                    <tr key={row.athleteId}>
                      <td className="numeric">{index + 1}</td>
                      <td>
                        <AthleteLink saveId={saveId} athleteId={row.athleteId} name={row.athleteName} />
                      </td>
                      <td className="numeric">{formatPoints(row.totalLeaguePointsThousandths)}</td>
                      <td className="numeric">{row.completedSeasons}</td>
                      <td className="numeric">{row.bestRank === 0 ? '—' : `P${row.bestRank}`}</td>
                      <td className="numeric">{row.titles}</td>
                      <td className="numeric">{row.podiums}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            {profile.allTimeLeaderboard.length > LEADERBOARD_PREVIEW ? (
              <p>
                <button type="button" className="ghost-button" onClick={() => setShowAll((value) => !value)}>
                  {showAll
                    ? 'Show top 10 only'
                    : `Show all ${profile.allTimeLeaderboard.length} athletes`}
                </button>
              </p>
            ) : null}
          </>
        )}
      </Card>

      <Card
        eyebrow="Records"
        title={`League records — ${profile.records.length}`}
        info={
          <p>
            Grounded exclusively in this league&apos;s completed official results. Tied holders share
            every record; vacancies and incomparable formats are omitted, never invented.
          </p>
        }
      >
        {profile.records.length === 0 ? (
          <Notice tone="empty" title="No records yet">
            <p>Records appear once this league completes a season.</p>
          </Notice>
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
                {profile.records.map((record) => (
                  <tr key={record.recordKey}>
                    <td>
                      {record.label}
                      {record.scopeNote ? <span className="card-sub"> · {record.scopeNote}</span> : null}
                    </td>
                    <td className="numeric">{record.valueDisplay}</td>
                    <td>
                      {record.holders.map((holder, holderIndex) => (
                        <span key={`${holder.athleteId}-${holderIndex}`}>
                          {holderIndex > 0 ? ', ' : ''}
                          <AthleteLink
                            saveId={saveId}
                            athleteId={holder.athleteId}
                            name={holder.athleteName}
                          />
                          <span className="card-sub"> {holderSeasons(holder.seasonNumbers)}</span>
                        </span>
                      ))}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>

      <p className="muted small">
        <Link to={leaguePath(saveId, profile.leagueKey)}>Copy link to this profile</Link>
        {' · '}
        <Link to={historyPath(saveId, {})}>Browse history</Link>
      </p>
    </div>
  );
}

/** Deterministic data-driven dynasty labels: titles, streaks and consistency only. */
function legendLines(profile: LeagueProfile | null): string[] {
  if (!profile) {
    return [];
  }
  const lines: string[] = [];
  const titles = profile.records.find((row) => row.recordKey === 'league_most_titles');
  if (titles && titles.value >= 2 && titles.holders.length > 0) {
    const names = titles.holders.map((holder) => holder.athleteName).slice(0, 3);
    lines.push(`${names.join(', ')} — ${titles.value} ${titles.value === 1 ? 'title' : 'titles'}`);
  }
  const streak = profile.records.find((row) => row.recordKey === 'league_longest_title_streak');
  if (streak && streak.value >= 2 && streak.holders.length > 0) {
    const names = streak.holders.map((holder) => holder.athleteName).slice(0, 3);
    lines.push(`${names.join(', ')} — ${streak.value} straight championships`);
  }
  const appearances = profile.records.find((row) => row.recordKey === 'league_most_appearances');
  if (appearances && appearances.value >= 3 && appearances.holders.length > 0) {
    const names = appearances.holders.map((holder) => holder.athleteName).slice(0, 3);
    lines.push(`${names.join(', ')} — ${appearances.value} seasons of consistency`);
  }
  return lines.slice(0, 3);
}

function holderSeasons(seasons: number[]): string {
  if (seasons.length === 0) {
    return '';
  }
  if (seasons.length === 1) {
    return `(Season ${seasons[0]})`;
  }
  if (seasons.length <= 4) {
    return `(Seasons ${seasons.join(', ')})`;
  }
  return `(Seasons ${seasons[0]}–${seasons[seasons.length - 1]} · ${seasons.length})`;
}
