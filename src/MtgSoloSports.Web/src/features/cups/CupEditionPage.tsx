import { useEffect, useState } from 'react';
import { apiErrorMessage } from '../../shared/api/http';
import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import { AthleteLink, Link } from '../routing/router';
import { cupEditionPath, cupsPath, historyPath, livePath, type CupKind } from '../routing/routes';
import { fetchColorCupIndividual, fetchColorCupTeam, type ColorCupIndividualResult } from './colorCupApi';
import { fetchCupEditions, optional, type CupEdition } from './cupHistoryApi';
import {
  cupKindOf,
  cupTitle,
  formatPoints,
  medalBadge,
  selectionKeyFor,
  stateLabel,
  teamEventKey,
  teamKeyFromName,
} from './cupFormat';
import { CupPodium } from './CupPodium';
import { fromColorTeamResult, fromTypeTeamResult, legsByGroup, type EditionTeamResult } from './editionModel';
import { fetchSelectionReport, type SelectionReport } from './selectionApi';
import { formatRating, selectedMembers } from './selectionExplain';
import { CardArt, SquadTiles } from './SquadTiles';
import { TeamBadge, TeamMark } from './TeamBadge';
import { fetchTypeCupTeam } from './typeCupApi';
import './CupHistory.css';

interface EditionData {
  editions: CupEdition[];
  report: SelectionReport | null;
  team: EditionTeamResult | null;
  individual: ColorCupIndividualResult | null;
}

async function loadEdition(saveId: string, cup: CupKind, season: number, signal: AbortSignal): Promise<EditionData> {
  const team =
    cup === 'color'
      ? optional(fetchColorCupTeam(saveId, season, signal)).then((result) => (result ? fromColorTeamResult(result) : null))
      : optional(fetchTypeCupTeam(saveId, season, signal)).then((result) => (result ? fromTypeTeamResult(result) : null));
  const individual = cup === 'color' ? optional(fetchColorCupIndividual(saveId, season, signal)) : Promise.resolve(null);
  const [listing, report, teamResult, individualResult] = await Promise.all([
    fetchCupEditions(saveId, signal),
    optional(fetchSelectionReport(saveId, selectionKeyFor(cup), season, signal)),
    team,
    individual,
  ]);
  return {
    editions: listing.editions.filter((edition) => cupKindOf(edition.cup) === cup),
    report,
    team: teamResult,
    individual: individualResult,
  };
}

/**
 * One Cup edition: champion, team table, every squad, the four group legs and,
 * for the Color Cup, the individual event. Read-only over stored results;
 * rounds are replayed in History and the Cup itself is played on Live.
 */
export function CupEditionPage({ saveId, cup, season }: { saveId: string; cup: CupKind; season: number }) {
  const [data, setData] = useState<EditionData | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    setData(null);
    setError(null);
    loadEdition(saveId, cup, season, controller.signal)
      .then(setData)
      .catch((failure: unknown) => {
        if (failure instanceof DOMException && failure.name === 'AbortError') {
          return;
        }
        setError(apiErrorMessage(failure));
      });
    return () => controller.abort();
  }, [saveId, cup, season]);

  if (error) {
    return (
      <Notice tone="error" title={`${cupTitle(cup)} unavailable`}>
        <p>{error}</p>
      </Notice>
    );
  }
  if (!data) {
    return <Loading label={`Loading ${cupTitle(cup)}…`} />;
  }

  const edition = data.editions.find((entry) => entry.sourceSeasonNumber === season);
  if (!edition || !data.report) {
    return (
      <Notice tone="warn" title="This Cup has not been played">
        <p>
          There is no {cupTitle(cup)} for Season {season} in this save.
        </p>
        <p>
          <Link to={cupsPath(saveId)} className="ghost-button">
            All Cups
          </Link>
        </p>
      </Notice>
    );
  }

  const { report, team, individual } = data;
  const seasons = data.editions.map((entry) => entry.sourceSeasonNumber).sort((a, b) => a - b);
  const previous = seasons.filter((value) => value < season).at(-1) ?? null;
  const next = seasons.find((value) => value > season) ?? null;
  const rankByTeam = new Map((team?.teams ?? []).map((entry) => [entry.teamKey, entry.teamRank]));
  const squads = report.teams
    .map((entry) => ({ teamKey: teamKeyFromName(cup, entry.teamName), teamName: entry.teamName, members: squadOf(entry) }))
    .sort(
      (a, b) =>
        (rankByTeam.get(a.teamKey) ?? Number.MAX_SAFE_INTEGER) - (rankByTeam.get(b.teamKey) ?? Number.MAX_SAFE_INTEGER) ||
        a.teamName.localeCompare(b.teamName),
    );
  const champion = team?.teams.find((entry) => entry.teamRank === 1) ?? null;
  const championSquad = champion ? squads.find((entry) => entry.teamKey === champion.teamKey) : undefined;
  const individualPodium = individual ? individual.standings.filter((row) => row.cupRank <= 3) : [];

  return (
    <div className="dashboard">
      <header className="cup-banner">
        <div className="cup-banner-head">
          <h1 className="cup-banner-title">
            {cupTitle(cup)} · Season {season}
          </h1>
          <nav className="cup-nav" aria-label="Cup editions">
            {previous !== null ? (
              <Link to={cupEditionPath(saveId, cup, previous)} className="ghost-button">
                ← Season {previous}
              </Link>
            ) : null}
            <Link to={cupsPath(saveId)} className="ghost-button">
              All Cups
            </Link>
            {next !== null ? (
              <Link to={cupEditionPath(saveId, cup, next)} className="ghost-button">
                Season {next} →
              </Link>
            ) : null}
          </nav>
        </div>
        <p className="muted">
          {stateLabel(edition.state)} · {edition.teamCount} teams
          {team ? ` · 4 rank groups × ${team.groupRounds} rounds` : ''}
        </p>
        {champion ? (
          <>
            <p>
              Champions:{' '}
              <TeamBadge saveId={saveId} cup={cup} teamKey={champion.teamKey} teamName={champion.teamName} large />
            </p>
            {championSquad ? <SquadTiles saveId={saveId} members={championSquad.members} /> : null}
          </>
        ) : (
          <p className="muted">The team event has not finished yet; the squads below are final.</p>
        )}
      </header>

      {team ? (
        <div className="page-grid">
          <Card eyebrow="Team event" title="Podium">
            <CupPodium
              entries={team.teams
                .filter((entry) => entry.teamRank <= 3)
                .map((entry) => ({
                  key: entry.teamKey,
                  place: entry.teamRank,
                  art: <TeamMark cup={cup} teamKey={entry.teamKey} teamName="" large />,
                  label: <TeamBadge saveId={saveId} cup={cup} teamKey={entry.teamKey} teamName={entry.teamName} />,
                  detail: formatPoints(entry.teamScoreThousandths),
                }))}
            />
          </Card>
          <Card
            eyebrow="Team event"
            title={`Final table — ${team.teams.length} teams`}
            info={
              <p>
                A team's score is the sum of its four group legs. Active bonus applies; no new bonus or league points
                are awarded. Checksum <code>{team.checksum.slice(0, 12)}</code>.
              </p>
            }
          >
            <div className="table-wrap">
              <table className="data-table">
                <thead>
                  <tr>
                    <th scope="col">Rank</th>
                    <th scope="col">Team</th>
                    <th scope="col">Score</th>
                    <th scope="col">Base</th>
                    <th scope="col">Group W</th>
                    <th scope="col">Round W</th>
                    <th scope="col">Medal</th>
                  </tr>
                </thead>
                <tbody>
                  {team.teams.map((entry) => (
                    <tr key={entry.teamKey}>
                      <td className="numeric">{entry.teamRank}</td>
                      <td>
                        <TeamBadge saveId={saveId} cup={cup} teamKey={entry.teamKey} teamName={entry.teamName} />
                      </td>
                      <td className="numeric">{formatPoints(entry.teamScoreThousandths)}</td>
                      <td className="numeric">{formatPoints(entry.teamBaseThousandths)}</td>
                      <td className="numeric">{entry.groupWins}</td>
                      <td className="numeric">{entry.roundWins}</td>
                      <td>{medalBadge(entry.medal)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </Card>
        </div>
      ) : null}

      <section className="page-section" aria-labelledby="edition-squads">
        <h2 id="edition-squads" className="section-title">
          Squads
        </h2>
        <p>
          <Link to={livePath(saveId, { event: selectionKeyFor(cup), season })} className="ghost-button">
            Why these athletes?
          </Link>
        </p>
        <div className="page-grid">
          {squads.map((squad) => (
            <Card
              key={squad.teamKey}
              eyebrow={rankByTeam.has(squad.teamKey) ? `Finished ${rankByTeam.get(squad.teamKey)}` : 'Squad'}
              title={squad.teamName}
              action={<TeamBadge saveId={saveId} cup={cup} teamKey={squad.teamKey} teamName="Team history" />}
            >
              <SquadTiles
                saveId={saveId}
                members={squad.members}
                renderDetail={(member) => <span>Rating {formatRating(member.finalRatingThousandths)}</span>}
              />
            </Card>
          ))}
        </div>
      </section>

      {team ? (
        <section className="page-section" aria-labelledby="edition-legs">
          <h2 id="edition-legs" className="section-title">
            Group legs
          </h2>
          <div className="page-grid">
            {legsByGroup(team.legs).map((group) => (
              <Card
                key={group.groupNumber}
                eyebrow={`Group ${group.groupNumber}`}
                title={`Squad #${group.groupNumber} athletes`}
                action={
                  <Link
                    to={historyPath(saveId, { season, event: teamEventKey(cup), group: group.groupNumber })}
                    className="ghost-button"
                  >
                    Replay
                  </Link>
                }
              >
                <div className="table-wrap">
                  <table className="data-table">
                    <thead>
                      <tr>
                        <th scope="col">Rank</th>
                        <th scope="col">Card</th>
                        <th scope="col">Team</th>
                        <th scope="col">Leg score</th>
                        <th scope="col">Round W</th>
                      </tr>
                    </thead>
                    <tbody>
                      {group.legs.map((leg) => (
                        <tr key={leg.athleteId}>
                          <td className="numeric">{leg.groupRank}</td>
                          <td>
                            <span className="roster-name">
                              <CardArt imageUrl={leg.imageUrl} name={leg.name} small />
                              <AthleteLink saveId={saveId} athleteId={leg.athleteId} name={leg.name} />
                            </span>
                          </td>
                          <td>
                            <TeamBadge saveId={saveId} cup={cup} teamKey={leg.teamKey} teamName={leg.teamName} />
                          </td>
                          <td className="numeric">{formatPoints(leg.groupScoreThousandths)}</td>
                          <td className="numeric">{leg.roundWins}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </Card>
            ))}
          </div>
        </section>
      ) : null}

      {cup === 'color' ? (
        <section className="page-section" aria-labelledby="edition-individual">
          <h2 id="edition-individual" className="section-title">
            Individual event
          </h2>
          {individual ? (
            <div className="page-grid">
              <Card eyebrow="Individual event" title={`${individual.championName} wins the Cup`}>
                <CupPodium
                  entries={individualPodium.map((row) => ({
                    key: String(row.athleteId),
                    place: row.cupRank,
                    art: <CardArt imageUrl={row.imageUrl} name={row.name} />,
                    label: <AthleteLink saveId={saveId} athleteId={row.athleteId} name={row.name} />,
                    detail: `${row.sportingColor} · ${formatPoints(row.cupScoreThousandths)}`,
                  }))}
                />
              </Card>
              <Card
                eyebrow="Individual event"
                title={`Full table — ${individual.standings.length} athletes`}
                action={
                  <Link to={historyPath(saveId, { season, event: 'color-cup-individual' })} className="ghost-button">
                    Replay
                  </Link>
                }
                info={<p>All selected athletes race one {individual.rounds}-round stage. No bonus is earned.</p>}
              >
                <div className="table-wrap">
                  <table className="data-table">
                    <thead>
                      <tr>
                        <th scope="col">Rank</th>
                        <th scope="col">Card</th>
                        <th scope="col">Team</th>
                        <th scope="col">Cup score</th>
                        <th scope="col">Round W</th>
                        <th scope="col">Medal</th>
                      </tr>
                    </thead>
                    <tbody>
                      {individual.standings.map((row) => (
                        <tr key={row.athleteId}>
                          <td className="numeric">{row.cupRank}</td>
                          <td>
                            <AthleteLink saveId={saveId} athleteId={row.athleteId} name={row.name} />
                            <span className="card-sub"> · #{row.selectionRank}</span>
                          </td>
                          <td>
                            <TeamBadge
                              saveId={saveId}
                              cup={cup}
                              teamKey={teamKeyFromName(cup, row.sportingColor)}
                              teamName={row.sportingColor}
                            />
                          </td>
                          <td className="numeric">{formatPoints(row.cupScoreThousandths)}</td>
                          <td className="numeric">{row.roundWins}</td>
                          <td>{medalBadge(row.medal)}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </Card>
            </div>
          ) : (
            <p className="muted">The individual event has not finished yet.</p>
          )}
        </section>
      ) : null}
    </div>
  );
}

function squadOf(team: SelectionReport['teams'][number]) {
  return selectedMembers(team).map((member) => ({
    athleteId: member.athleteId,
    name: member.name,
    imageUrl: member.imageUrl,
    selectionRank: member.selectionRank ?? member.rank,
    finalRatingThousandths: member.finalRatingThousandths,
  }));
}
