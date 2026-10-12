import { useState } from 'react';
import { Card } from '../../shared/ui/Card';
import { Link } from '../routing/router';
import { cupEditionPath } from '../routing/routes';
import { TeamBadge } from '../cups/TeamBadge';
import { formatPoints } from '../cups/cupFormat';
import type { TypeCupDraw, TypeCupTournament } from '../cups/typeCupTournamentApi';
import { qualificationStageLabel } from '../cups/typeCupTournamentModel';
import type { TypeCupStageInfo, TypeCupStageKey } from './typeCupLiveModel';
import { stageKeyEquals } from './typeCupLiveModel';

/**
 * Compact Type Cup stage navigation for Live (MSS-078).
 * One workspace per tournament stage: the nav lists Group A/B/C plus Final
 * with unambiguous completed/active/pending states (data-driven, never
 * hardcoded). Only the selected stage's completed summary appears here;
 * the Rounds card owns that stage's rounds, reveal and standings. Never
 * simulates; progression stays on the backend canonical order.
 */
export function TypeCupLiveStages({
  saveId,
  season,
  stages,
  selected,
  canonical,
  tournament,
  draw,
  complete,
  onSelectStage,
}: {
  saveId: string;
  season: number;
  stages: readonly TypeCupStageInfo[];
  selected: TypeCupStageKey | null;
  canonical: TypeCupStageKey | null;
  tournament: TypeCupTournament | null;
  draw: TypeCupDraw | null;
  complete: boolean;
  onSelectStage: (stage: TypeCupStageKey) => void;
}) {
  if (stages.length <= 1) {
    return null;
  }
  const groupCount = stages.filter((s) => s.kind === 'qualification').length;
  const selectedInfo = selected ? (stages.find((s) => stageKeyEquals(s.key, selected)) ?? null) : null;
  const canonicalInfo = canonical ? (stages.find((s) => stageKeyEquals(s.key, canonical)) ?? null) : null;
  const viewingOther = selected && canonical && !stageKeyEquals(selected, canonical);
  const selectedIndex = selectedInfo ? stages.findIndex((s) => stageKeyEquals(s.key, selectedInfo.key)) : -1;
  const nextStage = selectedIndex >= 0 && selectedIndex + 1 < stages.length ? stages[selectedIndex + 1]! : null;
  const doneCount = stages.filter((s) => s.status === 'completed').length;
  void draw;

  return (
    <Card
      eyebrow="Type Cup stages"
      title={`Stages · ${groupCount} groups + Final`}
      info={
        <p>
          Each qualification group and the Final is its own stage with fresh 1–32 rounds. Points never
          carry to the Final.
        </p>
      }
    >
      <p className="muted small">
        {doneCount} of {stages.length} stages complete
        {canonicalInfo && !complete ? ` · Active: ${canonicalInfo.label}` : null}
        {complete ? ' · Tournament complete' : null}
      </p>
      <div className="segmented" role="group" aria-label="Type Cup stages">
        {stages.map((stage) => {
          const active = selected ? stageKeyEquals(stage.key, selected) : false;
          const badgeClass =
            stage.status === 'completed' ? 'badge badge-done' : stage.status === 'active' ? 'badge badge-ready' : 'badge badge-wait';
          const statusText = stage.status === 'completed' ? 'Completed' : stage.status === 'active' ? 'Active' : 'Pending';
          return (
            <button
              key={`${stage.key.tournamentPhase}/${stage.key.qualificationGroup}`}
              type="button"
              className={active ? 'nav-item current' : 'nav-item'}
              aria-pressed={active}
              aria-label={`${stage.label}, ${statusText}, ${stage.played} of ${stage.total} rounds`}
              onClick={() => onSelectStage(stage.key)}
            >
              {stage.shortLabel} · <span className={badgeClass}>{statusText}</span>
            </button>
          );
        })}
      </div>
      {viewingOther && canonicalInfo ? (
        <p className="muted small">
          Viewing {selectedInfo?.label} (replay). Active: {canonicalInfo.label}.{' '}
          <button type="button" className="ghost-button" onClick={() => canonical && onSelectStage(canonical)}>
            Follow active stage
          </button>
        </p>
      ) : null}
      {selectedInfo ? (
        <SelectedStageSummary
          saveId={saveId}
          season={season}
          stage={selectedInfo}
          tournament={tournament}
          complete={complete}
          nextStage={nextStage}
          onSelectStage={onSelectStage}
        />
      ) : null}
    </Card>
  );
}

function SelectedStageSummary({
  saveId,
  season,
  stage,
  tournament,
  complete,
  nextStage,
  onSelectStage,
}: {
  saveId: string;
  stage: TypeCupStageInfo;
  season: number;
  tournament: TypeCupTournament | null;
  complete: boolean;
  nextStage: TypeCupStageInfo | null;
  onSelectStage: (stage: TypeCupStageKey) => void;
}) {
  const [open, setOpen] = useState(false);
  if (stage.kind === 'final') {
    if (tournament?.final) {
      return (
        <div>
          <p className="muted small">
            Final complete: {tournament.final.championCreatureType} wins the Type Cup.{' '}
            <Link to={cupEditionPath(saveId, 'type', season)} className="card-link">
              Open the Final result
            </Link>
          </p>
          <p className="muted small">
            <button type="button" className="ghost-button" aria-expanded={open} onClick={() => setOpen((v) => !v)}>
              {open ? 'Hide details' : 'Why this order?'}
            </button>
          </p>
          {open ? (
            <p className="muted small">
              Order and cutoff are persisted backend facts for the Final; qualification points reset
              before the Final and group victories carry no honours.
            </p>
          ) : null}
        </div>
      );
    }
    const qualsDone = (tournament?.qualificationGroups ?? []).length;
    const qualCount = tournament?.qualificationGroupCount ?? 0;
    if (!complete && qualsDone >= qualCount && qualCount > 0 && stage.played === 0) {
      return (
        <p className="muted small">
          Every qualification group has a persisted table. Press Next Round to start the Final —
          all scores reset to zero.
        </p>
      );
    }
    if (stage.played === 0) {
      return <p className="muted small">Final pending. Qualification groups complete in canonical draw order first.</p>;
    }
    return (
      <p className="muted small">
        Final in progress: Round {stage.played} / {stage.total}. Scores started fresh at zero.
      </p>
    );
  }
  const group = (tournament?.qualificationGroups ?? []).find((g) => g.qualificationGroup === stage.qualificationGroup);
  if (!group) {
    if (stage.status === 'completed') {
      return <p className="muted small">{stage.label} complete.</p>;
    }
    if (stage.played === 0) {
      return (
        <p className="muted small">
          {stage.label} pending{nextStage ? '' : ''}.{' '}
          {nextStage && stage.status !== 'active' ? (
            <button type="button" className="ghost-button" onClick={() => onSelectStage(nextStage.key)}>
              Continue to {nextStage.label}
            </button>
          ) : null}
        </p>
      );
    }
    return (
      <p className="muted small">
        {stage.label} in progress: Round {stage.played} / {stage.total}.
      </p>
    );
  }
  return (
    <div>
      <CompletedGroupBlock saveId={saveId} group={group} groupCount={tournament?.qualificationGroupCount ?? 0} />
      {nextStage ? (
        <p className="live-buttons">
          <button type="button" className="ghost-button" onClick={() => onSelectStage(nextStage.key)}>
            Continue to {nextStage.label}
          </button>
        </p>
      ) : null}
    </div>
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
