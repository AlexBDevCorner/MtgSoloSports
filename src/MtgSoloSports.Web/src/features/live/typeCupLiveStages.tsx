import { useState } from 'react';
import { Card } from '../../shared/ui/Card';
import { Link } from '../routing/router';
import { cupEditionPath } from '../routing/routes';
import { TeamBadge } from '../cups/TeamBadge';
import { formatPoints } from '../cups/cupFormat';
import type { TypeCupDraw, TypeCupTournament } from '../cups/typeCupTournamentApi';
import {
  qualificationGroupLetter,
  qualificationStageLabel,
} from '../cups/typeCupTournamentModel';
import type { EventProgress } from '../events/eventsApi';

/**
 * Tournament progression for a scalable Type Cup on Live (MSS-063).
 * Read-only over persisted draw/tournament facts plus the backend progress
 * cursor: which qualification group is active/completed/pending and the Final.
 * Completed groups show their final team table with an obvious cutoff;
 * detailed legs stay behind on-demand toggles so large tournaments stay
 * bounded. Fast progression stays on the backend canonical order via the
 * existing Run-remaining control; this view never simulates.
 */
export function TypeCupLiveStages({
  saveId,
  season,
  progress,
  tournament,
  draw,
  lastStage,
}: {
  saveId: string;
  season: number;
  progress: EventProgress | null;
  tournament: TypeCupTournament | null;
  draw: TypeCupDraw | null;
  lastStage: string | null;
}) {
  const groupCount =
    tournament?.qualificationGroupCount ??
    draw?.qualificationGroupCount ??
    progress?.qualificationGroupCount ??
    0;
  if (groupCount <= 0) {
    return null;
  }
  const activeQual = progress?.qualificationGroup ?? null;
  const activePhase = progress?.tournamentPhase ?? null;
  const doneGroups = new Set((tournament?.qualificationGroups ?? []).map((group) => group.qualificationGroup));
  const currentStage = lastStage ?? progress?.tournamentStage ?? null;

  return (
    <Card
      eyebrow="Type Cup tournament"
      title={`Qualification + Final · ${groupCount} groups`}
      info={
        <p>
          Qualification groups play in canonical draw order on the backend, then the fresh 32-team
          Final starts at zero. Completed tables below are persisted facts; the frontend never
          computes who qualified.
        </p>
      }
    >
      {currentStage ? <p className="muted small">Current stage: {currentStage}</p> : null}
      <ol className="edition-card-podium">
        {Array.from({ length: groupCount }, (_, index) => index + 1).map((qual) => {
          const letter = qualificationGroupLetter(qual);
          const done = doneGroups.has(qual);
          const active = activePhase === 1 && activeQual === qual;
          const status = done ? 'Completed' : active ? 'Active' : 'Pending';
          return (
            <li key={qual}>
              <span>
                Qualification Group {letter}
                {groupCount === 2 ? (qual === 1 ? ' · Semifinal A' : ' · Semifinal B') : ''} —{' '}
                <span className={done ? 'badge badge-done' : active ? 'badge badge-ready' : 'badge badge-wait'}>
                  {status}
                </span>
              </span>
            </li>
          );
        })}
        <li>
          <span>
            Type Cup Final —{' '}
            <span
              className={
                tournament?.final
                  ? 'badge badge-done'
                  : activePhase === 2
                    ? 'badge badge-ready'
                    : 'badge badge-wait'
              }
            >
              {tournament?.final ? 'Completed' : activePhase === 2 ? 'Active' : 'Pending'}
            </span>
          </span>
        </li>
      </ol>

      {(tournament?.qualificationGroups ?? []).map((group) => (
        <CompletedGroupBlock
          key={group.qualificationGroup}
          saveId={saveId}
          group={group}
          groupCount={groupCount}
        />
      ))}

      {tournament?.final ? (
        <p className="muted small">
          Final complete: {tournament.final.championCreatureType} wins the Type Cup.{' '}
          <Link to={cupEditionPath(saveId, 'type', season)} className="card-link">
            Open the Final result
          </Link>
        </p>
      ) : doneGroups.size === groupCount && groupCount > 0 ? (
        <p className="muted small">
          Every qualification group has a persisted table. Press Next Round to start the Final —
          all scores reset to zero.
        </p>
      ) : doneGroups.size > 0 ? (
        <p className="muted small">
          Continue with Next Round for the next qualification group — no Dashboard round trip
          needed. Completed tables above already show their qualification cutoffs.
        </p>
      ) : null}
    </Card>
  );
}

function CompletedGroupBlock({
  saveId,
  group,
  groupCount,
}: {
  saveId: string;
  group: NonNullable<TypeCupTournament['qualificationGroups']>[number];
  groupCount: number;
}) {
  const [open, setOpen] = useState(false);
  const stage = qualificationStageLabel(group.qualificationGroup, groupCount);
  const teams = [...group.teams].sort((a, b) => a.teamRank - b.teamRank);
  const guaranteed = group.guaranteedPlaces ?? group.finalPlaces;
  const advancing = group.wildcardCandidate != null
    ? `${guaranteed} guaranteed + wildcard candidate at rank ${guaranteed + 1}`
    : `${group.finalPlaces} advance`;
  return (
    <section className="team-season" aria-label={`${stage} final table`}>
      <div className="team-season-head">
        <strong>{stage} — final</strong>
        <span className="team-season-result">
          {group.groupSize} teams · {advancing}
        </span>
      </div>
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
              <tr key={team.creatureType}>
                <td className="numeric">{team.teamRank}</td>
                <td>
                  <TeamBadge saveId={saveId} cup="type" teamKey={team.creatureType} teamName={team.creatureType} />
                </td>
                <td className="numeric">{formatPoints(team.teamScoreThousandths)}</td>
                <td>
                  {team.qualified ? (
                    <span className="badge badge-ready">{team.qualificationStatus && team.qualificationStatus !== 'Eliminated' ? (team.qualificationStatus === 'Guaranteed' ? 'Qualified · guaranteed' : team.qualificationStatus === 'Wildcard' ? 'Qualified · wildcard' : 'Qualified') : 'Qualified'}</span>
                  ) : (
                    <span className="badge badge-wait">Eliminated</span>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <p className="muted small">
        {group.wildcardCandidate != null ? `Top ${guaranteed} guaranteed; rank ${guaranteed + 1} (${group.wildcardCandidate}) competes for a global wildcard${group.wildcardWinner ? ` — ${group.wildcardWinner} earned it` : ''}. ` : `Cut after place ${group.finalPlaces}. `}
        <button type="button" className="ghost-button" aria-expanded={open} onClick={() => setOpen((value) => !value)}>
          {open ? 'Hide details' : 'Why this order?'}
        </button>
      </p>
      {open ? (
        <p className="muted small">
          Order and cutoff are persisted backend facts for {stage}; qualification points reset
          before the Final and group victories carry no honours.
        </p>
      ) : null}
    </section>
  );
}
