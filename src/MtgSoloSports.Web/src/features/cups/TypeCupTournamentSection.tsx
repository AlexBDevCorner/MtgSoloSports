import { useState } from 'react';
import { Card } from '../../shared/ui/Card';
import { AthleteLink, Link } from '../routing/router';
import { historyPath, livePath, type CupKind } from '../routing/routes';
import { teamEventKey } from './cupFormat';
import { CupPodium } from './CupPodium';
import { CardArt } from './SquadTiles';
import { TeamBadge, TeamMark } from './TeamBadge';
import { formatPoints } from './cupFormat';
import { medalBadge } from './cupFormat';
import type {
  TypeCupDraw,
  TypeCupTournament,
  TypeCupTournamentLeg,
  TypeCupTournamentQualificationGroup,
} from './typeCupTournamentApi';
import {
  advancingLabel,
  isWildcardTournament,
  legsByRankGroup,
  orderedDrawMembers,
  orderedQualificationTeams,
  qualificationGroupLetter,
  qualificationStageLabel,
  qualificationStatusLabel,
  rankGroupLabel,
  tournamentStateLabel,
} from './typeCupTournamentModel';

/**
 * Tournament presentation for a completed Type Cup with qualification
 * (MSS-063). Read-only over the persisted tournament summary and draw:
 * overview, random persisted draw, per-group tables with an obvious cutoff,
 * the fresh 32-team Final, and stage-aware legs. Detailed legs render on
 * demand per group so large tournaments stay bounded.
 */
export function TypeCupTournamentSection({
  saveId,
  cup,
  season,
  tournament,
  draw,
}: {
  saveId: string;
  cup: CupKind;
  season: number;
  tournament: TypeCupTournament;
  draw: TypeCupDraw | null;
}) {
  const groupCount = tournament.qualificationGroupCount;
  const wildcard = isWildcardTournament(tournament);
  return (
    <>
      <Card
        eyebrow="Type Cup tournament"
        title={`${tournament.teamCount} teams · Qualification + Final`}
        info={
          <p>
            Qualification points reset: the Final starts every team at zero and only Final ranks
            1–3 hold official medals and honours. Qualification group victories are not major
            honours. {wildcard ? 'Each group has equal guaranteed places; remaining Final places are performance wildcards across groups.' : null}
          </p>
        }
      >
        <dl className="cup-facts">
          <div className="cup-fact">
            <dt>Total selected teams</dt>
            <dd>{tournament.teamCount}</dd>
          </div>
          <div className="cup-fact">
            <dt>Format</dt>
            <dd>{wildcard ? 'Qualification + Final · guaranteed + wildcards' : 'Qualification + Final'}</dd>
          </div>
          <div className="cup-fact">
            <dt>Qualification groups</dt>
            <dd>{groupCount}</dd>
          </div>
          <div className="cup-fact">
            <dt>Group sizes</dt>
            <dd>{tournament.groupSizes.join(' / ')}</dd>
          </div>
          <div className="cup-fact">
            <dt>Advancing per group</dt>
            <dd>{advancingLabel(tournament)}</dd>
          </div>
          <div className="cup-fact">
            <dt>State</dt>
            <dd>{tournamentStateLabel(tournament)}</dd>
          </div>
          <div className="cup-fact">
            <dt>Final field</dt>
            <dd>{tournament.final ? `${tournament.final.teamCount} teams` : '32 teams'}</dd>
          </div>
        </dl>
        {tournament.drawChecksum ? (
          <p className="muted small">
            Draw checksum <code>{tournament.drawChecksum.slice(0, 12)}</code> · Tournament checksum{' '}
            <code>{tournament.tournamentChecksum.slice(0, 12)}</code>
          </p>
        ) : null}
        {wildcard && tournament.wildcards && tournament.wildcards.length > 0 ? (
          <p className="muted small">
            Wildcards: {tournament.wildcards.map((entry) => `${entry.creatureType} (Group ${qualificationGroupLetter(entry.qualificationGroup)}, rank ${entry.groupRank}, ${formatPoints(entry.teamScoreThousandths)} pts)`).join(' · ')}
            {tournament.wildcards.some((entry) => entry.tieDraw) ? ' · decided by seeded draw after exactly tied adjusted scores' : ''}
          </p>
        ) : null}
      </Card>

      <Card
        eyebrow="Qualification draw"
        title={`Random draw — ${groupCount} groups`}
        info={
          <p>
            The draw is random and persisted before competition, not strength seeded. Reloading or
            reopening history never redraws it; the groups below are the stored draw.
          </p>
        }
      >
        {tournament.qualificationGroups.map((group) => (
          <QualificationDrawBlock
            key={group.qualificationGroup}
            saveId={saveId}
            cup={cup}
            group={group}
            groupCount={groupCount}
            drawMembers={draw?.groups.find((entry) => entry.qualificationGroup === group.qualificationGroup)?.creatureTypes ?? null}
          />
        ))}
      </Card>

      <section className="page-section" aria-labelledby="type-cup-qualification">
        <h2 id="type-cup-qualification" className="section-title">
          Qualification results
        </h2>
        <div className="page-grid">
          {tournament.qualificationGroups.map((group) => (
            <QualificationResultCard
              key={group.qualificationGroup}
              saveId={saveId}
              cup={cup}
              season={season}
              group={group}
              groupCount={groupCount}
            />
          ))}
        </div>
      </section>

      {tournament.final ? (
        <FinalSection saveId={saveId} cup={cup} season={season} tournament={tournament} />
      ) : (
        <Card eyebrow="Type Cup Final" title="Final pending">
          <p className="muted">
            Qualification is complete. The 32-team Final starts fresh at zero once every group has
            a persisted table.
          </p>
          <p>
            <Link to={livePath(saveId, { event: 'type-cup-team', season })} className="primary-button">
              Continue on Live
            </Link>
          </p>
        </Card>
      )}
    </>
  );
}

function QualificationDrawBlock({
  saveId,
  cup,
  group,
  groupCount,
  drawMembers,
}: {
  saveId: string;
  cup: CupKind;
  group: TypeCupTournamentQualificationGroup;
  groupCount: number;
  drawMembers: string[] | null;
}) {
  const letter = qualificationGroupLetter(group.qualificationGroup);
  const stage = qualificationStageLabel(group.qualificationGroup, groupCount);
  // Prefer the persisted draw order for the draw block; fall back to the
  // result teams when the draw endpoint has no row for this group.
  const members = orderedDrawMembers(drawMembers ?? group.teams.map((team) => team.creatureType));
  const guaranteed = group.guaranteedPlaces ?? group.finalPlaces;
  const advancing = group.wildcardCandidate != null
    ? `${guaranteed} guaranteed + wildcard candidate`
    : `${group.finalPlaces} advance`;
  return (
    <section className="team-season" aria-label={stage}>
      <div className="team-season-head">
        <strong>
          Qualification Group {letter}
          {groupCount === 2 ? (group.qualificationGroup === 1 ? ' · Semifinal A' : ' · Semifinal B') : ''}
        </strong>
        <span className="team-season-result">
          {group.groupSize} teams · {advancing}
        </span>
      </div>
      <ul className="edition-card-podium">
        {members.map((creatureType) => (
          <li key={creatureType}>
            <TeamBadge saveId={saveId} cup={cup} teamKey={creatureType} teamName={creatureType} />
          </li>
        ))}
      </ul>
    </section>
  );
}

function QualificationResultCard({
  saveId,
  cup,
  season,
  group,
  groupCount,
}: {
  saveId: string;
  cup: CupKind;
  season: number;
  group: TypeCupTournamentQualificationGroup;
  groupCount: number;
}) {
  const [showLegs, setShowLegs] = useState(false);
  const letter = qualificationGroupLetter(group.qualificationGroup);
  const stage = qualificationStageLabel(group.qualificationGroup, groupCount);
  const teams = orderedQualificationTeams(group);
  const guaranteed = group.guaranteedPlaces ?? group.finalPlaces;
  const hasWildcard = group.wildcardCandidate != null;
  const title = hasWildcard
    ? `Group ${letter} — ${group.groupSize} teams, top ${guaranteed} guaranteed + wildcard candidate at rank ${guaranteed + 1}`
    : `Group ${letter} — ${group.groupSize} teams, ${group.finalPlaces} advance`;
  return (
    <Card
      eyebrow={stage}
      title={title}
      info={
        <p>
          Final table of {stage}. {hasWildcard ? `Top ${guaranteed} qualify directly; rank ${guaranteed + 1} competes for a global wildcard on team points${group.wildcardWinner ? ` — ${group.wildcardWinner} earned it` : ''}. ` : 'The cut line follows the last qualifying place; teams above it reached the Final, teams below it were eliminated. '}Points do not carry to the Final.
        </p>
      }
    >
      <div className="table-wrap">
        <table className="data-table">
          <thead>
            <tr>
              <th scope="col" className="numeric">
                Rank
              </th>
              <th scope="col">Team</th>
              <th scope="col" className="numeric">
                Score
              </th>
              <th scope="col">Status</th>
            </tr>
          </thead>
          <tbody>
            {teams.map((team) => (
              <QualificationRow
                key={team.creatureType}
                saveId={saveId}
                cup={cup}
                team={team}
                isCutRow={team.teamRank === guaranteed}
                isBelowCut={team.teamRank === guaranteed + 1}
                guaranteed={guaranteed}
              />
            ))}
          </tbody>
        </table>
      </div>
      <p className="muted small">
        <button
          type="button"
          className="ghost-button"
          aria-expanded={showLegs}
          onClick={() => setShowLegs((value) => !value)}
        >
          {showLegs ? 'Hide group legs' : `Show group legs (${group.legs.length} athletes)`}
        </button>{' '}
        <Link
          to={historyPath(saveId, { season, event: teamEventKey(cup), group: 1 })}
          className="card-link"
          title="Replay rank-group rounds in History"
        >
          Replay rounds
        </Link>
      </p>
      {showLegs ? (
        <QualificationLegs saveId={saveId} cup={cup} group={group} groupCount={groupCount} season={season} />
      ) : null}
    </Card>
  );
}

function QualificationRow({
  saveId,
  cup,
  team,
  isCutRow,
  isBelowCut,
  guaranteed,
}: {
  saveId: string;
  cup: CupKind;
  team: { creatureType: string; teamRank: number; teamScoreThousandths: number; qualified: boolean; qualificationStatus?: string };
  isCutRow: boolean;
  isBelowCut: boolean;
  guaranteed?: number;
}) {
  const status = qualificationStatusLabel(team.qualificationStatus, team.qualified);
  const isWildcard = team.qualificationStatus === 'Wildcard';
  return (
    <>
      <tr>
        <td className="numeric">{team.teamRank}</td>
        <td>
          <TeamBadge saveId={saveId} cup={cup} teamKey={team.creatureType} teamName={team.creatureType} />
        </td>
        <td className="numeric">{formatPoints(team.teamScoreThousandths)}</td>
        <td>
          {team.qualified ? (
            <span className="badge badge-ready">{status}</span>
          ) : (
            <span className="badge badge-wait">{status}</span>
          )}
        </td>
      </tr>
      {isCutRow ? (
        <tr className="cut-line" aria-hidden="true">
          <td colSpan={4} className="cut-line-cell">
            <span className="cut-line-label">Guaranteed cut — top {team.teamRank} qualify{isWildcard || guaranteed !== undefined ? '' : ''}{guaranteed !== undefined && isBelowCut === false ? '' : ''}</span>
          </td>
        </tr>
      ) : null}
      {isBelowCut && team.qualified === false ? (
        <tr className="cut-line" aria-hidden="true">
          <td colSpan={4} className="cut-line-cell">
            <span className="cut-line-label">Wildcard candidate — rank {team.teamRank} competes across groups</span>
          </td>
        </tr>
      ) : null}
      {isBelowCut && team.qualified ? (
        <tr className="cut-line" aria-hidden="true">
          <td colSpan={4} className="cut-line-cell">
            <span className="cut-line-label">Wildcard winner — rank {team.teamRank} earned a Final place on points</span>
          </td>
        </tr>
      ) : null}
    </>
  );
}

function QualificationLegs({
  saveId,
  cup,
  group,
  groupCount,
  season,
}: {
  saveId: string;
  cup: CupKind;
  group: TypeCupTournamentQualificationGroup;
  groupCount: number;
  season: number;
}) {
  const letter = qualificationGroupLetter(group.qualificationGroup);
  const byRank = legsByRankGroup(group.legs);
  return (
    <div className="page-grid">
      {byRank.map((rank) => (
        <div key={rank.groupNumber} className="table-wrap">
          <h3 className="reveal-subhead">
            Qualification Group {letter} · {rankGroupLabel(rank.groupNumber)} athletes
          </h3>
          <table className="data-table">
            <thead>
              <tr>
                <th scope="col" className="numeric">
                  Rank
                </th>
                <th scope="col">Card</th>
                <th scope="col">Team</th>
                <th scope="col" className="numeric">
                  Leg score
                </th>
              </tr>
            </thead>
            <tbody>
              {rank.legs.map((leg) => (
                <LegRow key={leg.saveAthleteId} saveId={saveId} cup={cup} leg={leg} season={season} />
              ))}
            </tbody>
          </table>
        </div>
      ))}
      <p className="muted small">
        Rank groups are athlete squad positions (#1 vs #1, #2 vs #2, …), not qualification groups.
        Use the group heading above so a “Squad #1 rank” is never confused with Qualification Group{' '}
        {letter}.
      </p>
    </div>
  );
}

function LegRow({
  saveId,
  cup,
  leg,
  season,
}: {
  saveId: string;
  cup: CupKind;
  leg: TypeCupTournamentLeg;
  season: number;
}) {
  return (
    <tr>
      <td className="numeric">{leg.groupRank}</td>
      <td>
        <AthleteLink saveId={saveId} athleteId={leg.saveAthleteId} name={leg.athleteName} />
        <span className="card-sub"> · #{leg.selectionRank}</span>
      </td>
      <td>
        <TeamBadge saveId={saveId} cup={cup} teamKey={leg.creatureType} teamName={leg.creatureType} />
      </td>
      <td className="numeric">{formatPoints(leg.groupScoreThousandths)}</td>
    </tr>
  );
}

function FinalSection({
  saveId,
  cup,
  season,
  tournament,
}: {
  saveId: string;
  cup: CupKind;
  season: number;
  tournament: TypeCupTournament;
}) {
  const final = tournament.final!;
  const [showLegs, setShowLegs] = useState(false);
  const finalists = [...tournament.finalists].sort((a, b) => a.localeCompare(b));
  return (
    <>
      <Card
        eyebrow="Type Cup Final"
        title={`Final — ${final.teamCount} teams start at zero`}
        info={
          <p>
            The Final is a new sporting stage: qualification points reset and every finalist starts
            at zero. Gold, Silver and Bronze below are the authoritative Type Cup podium; the
            champion is the official Type Cup winner. Checksum <code>{final.checksum.slice(0, 12)}</code>.
          </p>
        }
      >
        <CupPodium
          entries={final.teams
            .filter((entry) => entry.teamRank <= 3)
            .map((entry) => ({
              key: entry.creatureType,
              place: entry.teamRank,
              art: <TeamMark cup={cup} teamKey={entry.creatureType} teamName="" large />,
              label: <TeamBadge saveId={saveId} cup={cup} teamKey={entry.creatureType} teamName={entry.creatureType} />,
              detail: formatPoints(entry.teamScoreThousandths),
            }))}
        />
        <p className="muted small">{finalists.length} finalists: {finalists.slice(0, 8).join(', ')}{finalists.length > 8 ? `, … +${finalists.length - 8} more` : ''}</p>
        <details>
          <summary>All {finalists.length} finalists</summary>
          <ul className="edition-card-podium">
            {finalists.map((creatureType) => (
              <li key={creatureType}>
                <TeamBadge saveId={saveId} cup={cup} teamKey={creatureType} teamName={creatureType} />
              </li>
            ))}
          </ul>
        </details>
      </Card>

      <Card
        eyebrow="Type Cup Final"
        title={`Final table — ${final.teams.length} teams`}
        info={
          <p>
            A Final team&apos;s score is the sum of its four Final legs, all starting at zero.
            Qualification scores never carry over.
          </p>
        }
      >
        <div className="table-wrap">
          <table className="data-table">
            <thead>
              <tr>
                <th scope="col" className="numeric">
                  Rank
                </th>
                <th scope="col">Team</th>
                <th scope="col" className="numeric">
                  Score
                </th>
                <th scope="col" className="numeric">
                  Base
                </th>
                <th scope="col">Medal</th>
              </tr>
            </thead>
            <tbody>
              {[...final.teams]
                .sort((a, b) => a.teamRank - b.teamRank)
                .map((entry) => (
                  <tr key={entry.creatureType}>
                    <td className="numeric">{entry.teamRank}</td>
                    <td>
                      <TeamBadge saveId={saveId} cup={cup} teamKey={entry.creatureType} teamName={entry.creatureType} />
                    </td>
                    <td className="numeric">{formatPoints(entry.teamScoreThousandths)}</td>
                    <td className="numeric">{formatPoints(entry.teamBaseThousandths)}</td>
                    <td>{medalBadge(entry.medal)}</td>
                  </tr>
                ))}
            </tbody>
          </table>
        </div>
        <p className="muted small">
          <button
            type="button"
            className="ghost-button"
            aria-expanded={showLegs}
            onClick={() => setShowLegs((value) => !value)}
          >
            {showLegs ? 'Hide Final legs' : `Show Final legs (${final.legs.length} athletes)`}
          </button>{' '}
          <Link
            to={historyPath(saveId, { season, event: teamEventKey(cup), group: 1 })}
            className="card-link"
            title="Replay Final rank-group rounds in History"
          >
            Replay Final rounds
          </Link>
        </p>
        {showLegs ? (
          <div className="page-grid">
            {legsByRankGroup(final.legs).map((rank) => (
              <div key={rank.groupNumber} className="table-wrap">
                <h3 className="reveal-subhead">Type Cup Final · {rankGroupLabel(rank.groupNumber)} athletes</h3>
                <table className="data-table">
                  <thead>
                    <tr>
                      <th scope="col" className="numeric">
                        Rank
                      </th>
                      <th scope="col">Card</th>
                      <th scope="col">Team</th>
                      <th scope="col" className="numeric">
                        Leg score
                      </th>
                    </tr>
                  </thead>
                  <tbody>
                    {rank.legs.map((leg) => (
                      <LegRow key={leg.saveAthleteId} saveId={saveId} cup={cup} leg={leg} season={season} />
                    ))}
                  </tbody>
                </table>
              </div>
            ))}
          </div>
        ) : null}
      </Card>
    </>
  );
}
