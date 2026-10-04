import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import { Link } from '../routing/router';
import { dashboardPath, livePath, savesPath } from '../routing/routes';
import { medalBadge } from '../cups/cupFormat';
import type { AthleteProfileState } from './useAthleteProfile';

/** Display-only projection of a fixed-point thousandths value (no sporting math). */
function formatPoints(thousandths: number): string {
  return (thousandths / 1000).toFixed(3);
}

/** Display-only projection of a fixed-point bonus (no sporting math). */
function formatBonus(thousandths: number): string {
  const sign = thousandths >= 0 ? '+' : '';
  return `${sign}${(thousandths / 1000).toFixed(3)}%`;
}

export function AthleteProfilePage({
  saveId,
  athleteId,
  rawAthleteId,
  state,
}: {
  saveId: string;
  athleteId: number | null;
  rawAthleteId?: string;
  state: AthleteProfileState;
}) {
  if (athleteId === null) {
    return (
      <Notice tone="error" title="Invalid athlete link">
        <p>
          {rawAthleteId ? `“${rawAthleteId}” is not a valid athlete id. ` : ''}Athlete ids are
          positive integers. Open a card row to find a valid career profile.
        </p>
        <p className="live-buttons">
          <Link to={dashboardPath(saveId)} className="ghost-button">
            Back to dashboard
          </Link>{' '}
          <Link to={livePath(saveId)} className="ghost-button">
            Back to live
          </Link>
        </p>
      </Notice>
    );
  }

  if (state.loading && !state.profile) {
    return <Loading label="Loading athlete profile…" />;
  }

  if (state.notFound || (state.error && !state.profile)) {
    return (
      <Notice tone="error" title="Athlete unavailable">
        <p>{state.error ?? 'That athlete no longer exists.'}</p>
        <p className="live-buttons">
          <Link to={livePath(saveId)} className="ghost-button">
            Back to live
          </Link>{' '}
          <Link to={dashboardPath(saveId)} className="ghost-button">
            Back to dashboard
          </Link>{' '}
          <Link to={savesPath()} className="ghost-button">
            Back to saves
          </Link>
        </p>
      </Notice>
    );
  }

  const profile = state.profile;
  if (!profile) {
    return <Loading label="Loading athlete profile…" />;
  }

  const { card, career, seasons, honours, movements, cupSelections } = profile;
  const cupHistory = profile.cupHistory ?? [];
  const statusLabel = career.isActive
    ? `Active · ${career.currentLeagueName ?? 'League'}`
    : 'Common pool';

  return (
    <div className="dashboard">
      <Card
        eyebrow="Athlete profile"
        title={card.name}
        action={
          <span className="live-buttons">
            <Link to={livePath(saveId)} className="ghost-button">
              Back to live
            </Link>{' '}
            <Link to={dashboardPath(saveId)} className="ghost-button">
              Dashboard
            </Link>
          </span>
        }
        info={
          <p>
            Card artwork is referenced by URL from the save snapshot and never downloaded by the
            simulation. When artwork is unavailable the initials fallback keeps the layout stable.
          </p>
        }
      >
        <div className="athlete-hero">
          {card.imageUrl ? (
            <img className="card-art-large" src={card.imageUrl} alt="" loading="lazy" />
          ) : (
            <span className="card-art-large card-art-fallback" aria-hidden="true">
              {card.name.slice(0, 2).toUpperCase()}
            </span>
          )}
          <span className="card-identity">
            <span className="card-name">{card.typeLine}</span>
            <span className="card-sub">
              {card.sportingColorName} · {card.manaCost || 'No cost'} ·{' '}
              {card.creatureTypes.join(', ') || 'Unknown type'}
              {card.setCode ? ` · ${card.setCode}` : ''}
            </span>
            <span className="card-sub">{statusLabel}</span>
            <span className="card-sub">
              Type Cup nationality: {card.typeCupNationality ?? 'Uncapped — eligible for any printed type'}
            </span>
          </span>
        </div>
        {state.error ? (
          <Notice tone="warn" title="Showing last loaded state">
            <p>{state.error}</p>
          </Notice>
        ) : null}
      </Card>

      <div className="page-grid">
        <Card eyebrow="Career" title={`${career.seasonsActive} season(s) active`}>
          <dl className="stats">
            <div>
              <dt>Status</dt>
              <dd>{statusLabel}</dd>
            </div>
            <div>
              <dt>Round wins</dt>
              <dd>{career.roundWins}</dd>
            </div>
            <div>
              <dt>Stage wins</dt>
              <dd>{career.stageWins}</dd>
            </div>
            <div>
              <dt>Stage podiums</dt>
              <dd>
                {career.stagePodiums} ({career.stageWins}W · {career.stageSeconds}2nd ·{' '}
                {career.stageThirds}3rd)
              </dd>
            </div>
            <div>
              <dt>Best finish</dt>
              <dd>
                {career.bestSeasonFinish !== null && career.bestSeasonNumber !== null
                  ? `P${career.bestSeasonFinish} (Season ${career.bestSeasonNumber})`
                  : '—'}
              </dd>
            </div>
          </dl>
        </Card>

        <Card
          eyebrow="Bonus"
          title="Effective vs lifetime"
          info={
            <p>
              Effective bonus activates from the next stage; Stage 32 bonus enters the next season
              at 80% weight. Lifetime earned never decays.
            </p>
          }
        >
          <dl className="stats">
            <div>
              <dt>Current effective</dt>
              <dd>{formatBonus(career.currentEffectiveBonusThousandths)}</dd>
            </div>
            <div>
              <dt>Lifetime earned</dt>
              <dd>{formatBonus(career.lifetimeEarnedBonusThousandths)}</dd>
            </div>
            <div>
              <dt>Updated through</dt>
              <dd>
                Season {career.lastSeasonNumber} · Stage {career.lastStageNumber}
              </dd>
            </div>
          </dl>
        </Card>

        <Card
          eyebrow="Identity"
          title="Sporting metadata"
          info={
            <p>
              Once an athlete appears in a Type Cup for a type, that nationality is permanent and
              the athlete can never represent another type.
            </p>
          }
        >
          <dl className="stats">
            <div>
              <dt>Sporting color</dt>
              <dd>{card.sportingColorName}</dd>
            </div>
            <div>
              <dt>Athlete id</dt>
              <dd>{profile.athleteId}</dd>
            </div>
            <div>
              <dt>Front colors</dt>
              <dd>{card.frontColors || '—'}</dd>
            </div>
            <div>
              <dt>Type Cup nationality</dt>
              <dd>{card.typeCupNationality ?? 'Uncapped'}</dd>
            </div>
            <div>
              <dt>Creature types</dt>
              <dd>{card.creatureTypes.join(', ') || '—'}</dd>
            </div>
          </dl>
        </Card>

        <Card eyebrow="Honours" title={`Career honours — ${honours.length}`}>
          {honours.length === 0 ? (
            <p className="muted">
              No official honours yet. Feeder and Superleague podiums (1st/2nd/3rd) plus Color Cup
              and Type Cup podiums persist here.
            </p>
          ) : (
            <div className="table-wrap">
              <table className="data-table">
                <thead>
                  <tr>
                    <th scope="col" className="numeric">Season</th>
                    <th scope="col">League</th>
                    <th scope="col">Honour</th>
                  </tr>
                </thead>
                <tbody>
                  {honours.map((honour, index) => (
                    <tr key={`${honour.seasonNumber}-${honour.leagueName}-${honour.honourKind}-${index}`}>
                      <td className="numeric">{honour.seasonNumber}</td>
                      <td>{honour.leagueName}</td>
                      <td>{honour.honourKind}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </Card>

        <Card eyebrow="Movements" title={`Promotion · relegation · pool — ${movements.length}`}>
          {movements.length === 0 ? (
            <p className="muted">
              No postseason movements yet. Superleague promotion, relegation, qualifier entries
              and pool returns appear here.
            </p>
          ) : (
            <div className="table-wrap">
              <table className="data-table">
                <thead>
                  <tr>
                    <th scope="col" className="numeric">Season</th>
                    <th scope="col">Movement</th>
                    <th scope="col">From → To</th>
                  </tr>
                </thead>
                <tbody>
                  {movements.map((movement, index) => (
                    <tr key={`${movement.toSeasonNumber}-${movement.kind}-${index}`}>
                      <td className="numeric">
                        {movement.fromSeasonNumber} → {movement.toSeasonNumber}
                      </td>
                      <td>
                        <span className="badge badge-wait">{movement.kind}</span>
                        {movement.fromSeasonRank > 0 ? (
                          <span className="card-sub"> · P{movement.fromSeasonRank}</span>
                        ) : null}
                      </td>
                      <td>
                        {movement.fromLeagueName} → {movement.toLeagueName}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </Card>

        <Card
          eyebrow="Selections"
          title={`Cup selections — ${cupSelections.length}`}
          info={<p>Selection order is #1–#4 by rating within each color or creature-type team.</p>}
        >
          {cupSelections.length === 0 ? (
            <p className="muted">
              No Cup selections yet. Color Cup picks four athletes per sporting color; Type Cup
              allocation respects permanent nationality.
            </p>
          ) : (
            <div className="table-wrap">
              <table className="data-table">
                <thead>
                  <tr>
                    <th scope="col" className="numeric">Season</th>
                    <th scope="col">Cup</th>
                    <th scope="col">Team</th>
                    <th scope="col" className="numeric">Rank</th>
                  </tr>
                </thead>
                <tbody>
                  {cupSelections.map((selection, index) => (
                    <tr key={`${selection.cupKind}-${selection.sourceSeasonNumber}-${selection.team}-${index}`}>
                      <td className="numeric">{selection.sourceSeasonNumber}</td>
                      <td>{selection.cupKind}</td>
                      <td>{selection.team}</td>
                      <td className="numeric">#{selection.selectionRank}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </Card>

        <Card
          eyebrow="Records"
          title={state.holdingsLoading ? 'Record context' : `Record context — ${state.recordHoldings.length} held`}
          info={<p>Computed from normalized projections without decompressing round payloads.</p>}
        >
          {state.holdingsLoading && state.recordHoldings.length === 0 ? (
            <p className="muted">Loading record holdings…</p>
          ) : state.recordHoldings.length === 0 ? (
            <p className="muted">
              Holds no outright career records right now. Ties share records; only an outright
              higher value replaces holders.
            </p>
          ) : (
            <div className="table-wrap">
              <table className="data-table">
                <thead>
                  <tr>
                    <th scope="col">Record</th>
                    <th scope="col" className="numeric">Value</th>
                  </tr>
                </thead>
                <tbody>
                  {state.recordHoldings.map((record) => (
                    <tr key={record.recordKey}>
                      <td>{record.label}</td>
                      <td className="numeric">{record.valueDisplay}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          {state.holdingsError ? (
            <p className="muted">Record holdings unavailable: {state.holdingsError}</p>
          ) : null}
        </Card>

        <Card
          eyebrow="History"
          title={`Season-by-season — ${seasons.length} season(s)`}
          info={
            <p>
              Season summaries are transactional projections rebuilt from authoritative stage and
              season standings; round-by-round replay stays in Live rounds.
            </p>
          }
        >
          <div className="table-wrap">
            <table className="data-table">
              <thead>
                <tr>
                    <th scope="col" className="numeric">Season</th>
                    <th scope="col">Status</th>
                    <th scope="col" className="numeric">Finish</th>
                    <th scope="col" className="numeric">Round W</th>
                    <th scope="col" className="numeric">Stage W</th>
                    <th scope="col" className="numeric">2nd/3rd</th>
                    <th scope="col" className="numeric">Earned</th>
                    <th scope="col" className="numeric">Champ pts</th>
                </tr>
              </thead>
              <tbody>
                {seasons.map((season) => (
                  <tr key={season.seasonNumber}>
                    <td className="numeric">{season.seasonNumber}</td>
                    <td>{season.wasActive ? (season.leagueName ?? 'Active') : 'Pool'}</td>
                    <td className="numeric">
                      {season.seasonRank !== null ? `P${season.seasonRank}` : '—'}
                      {season.isChampion ? ' · Champion' : ''}
                    </td>
                    <td className="numeric">{season.roundWins}</td>
                    <td className="numeric">{season.stageWins}</td>
                    <td className="numeric">
                      {season.stageSeconds}/{season.stageThirds}
                    </td>
                    <td className="numeric">{formatBonus(season.earnedBonusThousandths)}</td>
                    <td className="numeric">
                      {formatPoints(season.totalChampionshipPointsThousandths)}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </Card>

        <Card
          eyebrow="Cups"
          title={`Cup history — ${cupHistory.length} appearance(s)`}
          info={
            <p>
              Every completed Cup is its own row: season, event, team, place and result. Team rows
              show the team&apos;s final place; the leg detail is the athlete&apos;s own group
              contribution. Newest seasons first.
            </p>
          }
        >
          {cupHistory.length === 0 ? (
            <p className="muted">
              No Cup appearances yet. Completed Color Cup (individual and team) and Type Cup (team)
              results appear here automatically.
            </p>
          ) : (
            <div className="table-wrap">
              <table className="data-table">
                <thead>
                  <tr>
                    <th scope="col" className="numeric">Season</th>
                    <th scope="col">Cup / Event</th>
                    <th scope="col">Team</th>
                    <th scope="col" className="numeric">Place</th>
                    <th scope="col">Result</th>
                  </tr>
                </thead>
                <tbody>
                  {cupHistory.map((entry, index) => (
                    <tr
                      key={`${entry.sourceSeasonNumber}-${entry.cup}-${entry.event}-${entry.teamKey}-${index}`}
                    >
                      <td className="numeric">{entry.sourceSeasonNumber}</td>
                      <td>
                        {entry.eventName}{' '}
                        <span className="badge badge-wait">{entry.event}</span>
                      </td>
                      <td>{entry.teamName}</td>
                      <td className="numeric">
                        P{entry.place}
                        {entry.event === 'Team' ? (
                          <span className="card-sub">
                            {' '}
                            · team
                            {entry.groupRank !== null && entry.groupRank !== undefined
                              ? ` · leg P${entry.groupRank}`
                              : ''}
                          </span>
                        ) : (
                          <span className="card-sub"> · individual</span>
                        )}
                      </td>
                      <td>{medalBadge(entry.medal)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </Card>

        <Card eyebrow="Stories" title="Sporting stories">
          {state.storiesLoading && state.stories.length === 0 ? (
            <p className="muted">Loading stories…</p>
          ) : state.stories.length === 0 ? (
            <p className="muted">
              No stories yet for this athlete. First stage wins, titles, Superleague
              milestones and pool returns appear here.
            </p>
          ) : (
            <ul className="story-list">
              {state.stories.map((story) => (
                <li key={story.id}>
                  <span className="badge badge-ready">{story.eventType}</span>{' '}
                  <span>{story.text}</span>
                </li>
              ))}
            </ul>
          )}
          {state.storiesError ? (
            <p className="muted">Stories unavailable: {state.storiesError}</p>
          ) : null}
        </Card>
      </div>
    </div>
  );
}
