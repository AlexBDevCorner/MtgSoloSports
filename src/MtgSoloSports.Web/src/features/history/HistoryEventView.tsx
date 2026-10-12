import { useEffect, useMemo, useState } from 'react';
import { apiErrorMessage } from '../../shared/api/http';
import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import { optionalTournament } from '../cups/typeCupTournamentApi';
import type { TypeCupTournament } from '../cups/typeCupTournamentApi';
import {
  fetchEventRound,
  fetchEventRounds,
  fetchEventTeamStandings,
  type EventRoundView,
  type EventTeamStandings,
  type PlayedRound,
} from '../events/eventsApi';
import { EVENT_TITLES, isTeamEvent, roundLabel, type EventKey } from '../events/eventModel';
import { RoundReveal } from '../reveal/RoundReveal';
import { Link } from '../routing/router';
import { cupEditionPath, qualifiersPath } from '../routing/routes';

/** Display-only projection of a fixed-point thousandths value (no sporting math). */
function formatPoints(thousandths: number): string {
  return (thousandths / 1000).toFixed(3);
}

/**
 * History replay of a postseason event played in rounds: group/round
 * selection, the shared reveal of the stored round, and team standings for
 * team events. Reads persisted payloads only; never resimulates.
 */
export function HistoryEventView({
  saveId,
  season,
  event,
  urlGroup,
  urlRound,
  urlTournamentPhase,
  urlQualificationGroup,
  onChange,
}: {
  saveId: string;
  season: number;
  event: EventKey;
  urlGroup: number | null;
  urlRound: number | null;
  urlTournamentPhase?: number | null;
  urlQualificationGroup?: number | null;
  onChange: (
    group: number | null,
    round: number,
    stage?: { tournamentPhase: number; qualificationGroup: number } | null,
  ) => void;
}) {
  const [rounds, setRounds] = useState<PlayedRound[] | null>(null);
  const [roundView, setRoundView] = useState<EventRoundView | null>(null);
  const [teams, setTeams] = useState<EventTeamStandings | null>(null);
  const [tournament, setTournament] = useState<TypeCupTournament | null>(null);
  const [error, setError] = useState<string | null>(null);
  const team = isTeamEvent(event);
  const isTypeTournamentHistory = event === 'type-cup-team';

  useEffect(() => {
    const controller = new AbortController();
    setRounds(null);
    fetchEventRounds(saveId, season, event, controller.signal)
      .then(setRounds)
      .catch((failure: unknown) => {
        if (!(failure instanceof DOMException && failure.name === 'AbortError')) {
          setRounds([]);
          setError(apiErrorMessage(failure));
        }
      });
    if (team) {
      fetchEventTeamStandings(saveId, season, event, controller.signal)
        .then(setTeams)
        .catch(() => setTeams(null));
    } else {
      setTeams(null);
    }
    if (isTypeTournamentHistory) {
      optionalTournament(saveId, season, controller.signal)
        .then(setTournament)
        .catch(() => setTournament(null));
    } else {
      setTournament(null);
    }
    return () => controller.abort();
  }, [saveId, season, event, team, isTypeTournamentHistory]);

  const isTypeCup = event === 'type-cup-team';
  const urlStage =
    urlTournamentPhase !== null &&
    urlTournamentPhase !== undefined &&
    urlQualificationGroup !== null &&
    urlQualificationGroup !== undefined
      ? { tournamentPhase: urlTournamentPhase, qualificationGroup: urlQualificationGroup }
      : null;

  const selected: PlayedRound | null = useMemo(() => {
    if (!rounds || rounds.length === 0) {
      return null;
    }
    const wantedGroup = team ? urlGroup : null;
    if (urlRound !== null) {
      const staged = isTypeCup && urlStage
        ? rounds.find(
            (r) =>
              r.round === urlRound &&
              r.group === wantedGroup &&
              (r.tournamentPhase ?? null) === urlStage.tournamentPhase &&
              (r.qualificationGroup ?? null) === urlStage.qualificationGroup,
          )
        : undefined;
      if (staged) {
        return staged;
      }
      // Legacy group/round-only URLs: exact single match wins, otherwise fall
      // back to the latest stage so old links never replay the wrong stage
      // silently (the fetch below surfaces the ambiguity as a clear error).
      const candidates = rounds.filter((r) => r.round === urlRound && r.group === wantedGroup);
      if (candidates.length === 1) {
        return candidates[0]!;
      }
      if (candidates.length > 1 && !urlStage) {
        return candidates[candidates.length - 1]!;
      }
      const match = candidates[0];
      if (match) {
        return match;
      }
    }
    return rounds[rounds.length - 1]!;
    // urlStage is derived from urlTournamentPhase/urlQualificationGroup.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rounds, urlRound, urlGroup, team, isTypeCup, urlTournamentPhase, urlQualificationGroup]);

  useEffect(() => {
    if (!selected) {
      setRoundView(null);
      return;
    }
    const controller = new AbortController();
    setError(null);
    const stage =
      selected.tournamentPhase !== null &&
      selected.tournamentPhase !== undefined &&
      selected.qualificationGroup !== null &&
      selected.qualificationGroup !== undefined
        ? { tournamentPhase: selected.tournamentPhase, qualificationGroup: selected.qualificationGroup }
        : (urlStage ?? null);
    fetchEventRound(saveId, season, event, selected.round, selected.group, controller.signal, stage)
      .then(setRoundView)
      .catch((failure: unknown) => {
        if (!(failure instanceof DOMException && failure.name === 'AbortError')) {
          setError(apiErrorMessage(failure));
        }
      });
    return () => controller.abort();
  }, [saveId, season, event, selected]);

  if (rounds === null) {
    return <Loading label="Loading event…" />;
  }

  const groups = team ? Array.from(new Set(rounds.map((r) => r.group ?? 1))) : [];
  const visibleGroup = selected?.group ?? null;
  const selectedStage =
    selected?.tournamentPhase !== null &&
    selected?.tournamentPhase !== undefined &&
    selected?.qualificationGroup !== null &&
    selected?.qualificationGroup !== undefined
      ? { tournamentPhase: selected.tournamentPhase, qualificationGroup: selected.qualificationGroup }
      : urlStage;
  const stages = isTypeCup
    ? Array.from(
        new Map(
          rounds
            .filter(
              (r) =>
                r.tournamentPhase !== null &&
                r.tournamentPhase !== undefined &&
                r.qualificationGroup !== null &&
                r.qualificationGroup !== undefined,
            )
            .map(
              (r) =>
                [
                  `${r.tournamentPhase}/${r.qualificationGroup}`,
                  { tournamentPhase: r.tournamentPhase!, qualificationGroup: r.qualificationGroup! },
                ] as const,
            ),
        ).values(),
      )
    : [];
  const groupRounds =
    isTypeCup && selectedStage
      ? rounds.filter(
          (r) =>
            r.group === visibleGroup &&
            (r.tournamentPhase ?? null) === selectedStage.tournamentPhase &&
            (r.qualificationGroup ?? null) === selectedStage.qualificationGroup,
        )
      : rounds.filter((r) => r.group === visibleGroup);

  return (
    <>
      <div className="toolbar" role="group" aria-label="Event round">
        {event === 'qualifier' ? (
          <p className="muted small">
            This replay covers the Superleague qualifier rounds. Tiered seasons resolve 16 more
            feeder qualifiers —{' '}
            <Link to={qualifiersPath(saveId, { season })}>open all qualifiers with boundary and color</Link>.
          </p>
        ) : null}
        {isTypeTournamentHistory && tournament && !tournament.isDirectFinal ? (
          <p className="muted small">
            Rank groups below are squad positions (#1 vs #1, …) inside one stage — not the
            qualification groups themselves. Qualification draw, per-group tables with cutoffs and
            the fresh 32-team Final live on the{' '}
            <Link to={cupEditionPath(saveId, 'type', season)}>Type Cup edition page</Link>.
          </p>
        ) : null}
        {isTypeCup && stages.length > 1 ? (
          <label className="field">
            <span>Tournament stage</span>
            <select
              value={selectedStage ? `${selectedStage.tournamentPhase}/${selectedStage.qualificationGroup}` : ''}
              disabled={stages.length === 0}
              onChange={(changed) => {
                const [phaseRaw, qualRaw] = changed.target.value.split('/');
                const tournamentPhase = Number.parseInt(phaseRaw ?? '', 10);
                const qualificationGroup = Number.parseInt(qualRaw ?? '', 10);
                if (Number.isNaN(tournamentPhase) || Number.isNaN(qualificationGroup)) {
                  return;
                }
                const last = rounds
                  .filter(
                    (r) =>
                      (r.tournamentPhase ?? null) === tournamentPhase &&
                      (r.qualificationGroup ?? null) === qualificationGroup &&
                      r.group === visibleGroup,
                  )
                  .at(-1);
                if (last) {
                  onChange(visibleGroup, last.round, { tournamentPhase, qualificationGroup });
                } else {
                  onChange(visibleGroup, selected?.round ?? 1, { tournamentPhase, qualificationGroup });
                }
              }}
            >
              {stages.map((stage) => (
                <option key={`${stage.tournamentPhase}/${stage.qualificationGroup}`} value={`${stage.tournamentPhase}/${stage.qualificationGroup}`}>
                  {stage.tournamentPhase === 2
                    ? 'Final'
                    : stage.tournamentPhase === 0
                      ? 'Single field'
                      : `Qualification ${stage.qualificationGroup}`}
                </option>
              ))}
            </select>
          </label>
        ) : null}
        {team ? (
          <label className="field">
            <span>{isTypeTournamentHistory && tournament && !tournament.isDirectFinal ? 'Squad rank group' : 'Group'}</span>
            <select
              value={visibleGroup ?? ''}
              disabled={groups.length === 0}
              onChange={(changed) => {
                const group = Number.parseInt(changed.target.value, 10);
                const last = groupRounds.filter((r) => r.group === group).at(-1) ?? rounds.filter((r) => r.group === group).at(-1);
                if (last) {
                  const stage =
                    last.tournamentPhase !== null &&
                    last.tournamentPhase !== undefined &&
                    last.qualificationGroup !== null &&
                    last.qualificationGroup !== undefined
                      ? { tournamentPhase: last.tournamentPhase, qualificationGroup: last.qualificationGroup }
                      : (selectedStage ?? null);
                  onChange(group, last.round, stage);
                }
              }}
            >
              {groups.map((group) => (
                <option key={group} value={group}>
                  Group {group}
                </option>
              ))}
            </select>
          </label>
        ) : null}
        <label className="field">
          <span>Round</span>
          <select
            value={selected?.round ?? ''}
            disabled={groupRounds.length === 0}
            onChange={(changed) => {
              const round = Number.parseInt(changed.target.value, 10);
              if (!Number.isNaN(round)) {
                onChange(visibleGroup, round, selectedStage ?? null);
              }
            }}
          >
            {groupRounds.map((r) => (
              <option
                key={`${r.tournamentPhase ?? 'x'}/${r.qualificationGroup ?? 'x'}/${r.group ?? 0}-${r.round}`}
                value={r.round}
              >
                Round {r.round}
              </option>
            ))}
          </select>
        </label>
      </div>

      {error ? (
        <Notice tone="error" title="Replay unavailable">
          <p>{error}</p>
        </Notice>
      ) : null}

      {rounds.length === 0 ? (
        <Card eyebrow="Postseason event" title={EVENT_TITLES[event]}>
          <Notice tone="empty" title="No rounds played">
            <p>Play this event on the Live tab, then replay its rounds here.</p>
          </Notice>
        </Card>
      ) : roundView ? (
        <RoundReveal
          placements={roundView.placements}
          revealKey={`${event}:${season}:${roundView.tournamentPhase ?? 'x'}:${roundView.qualificationGroup ?? 'x'}:${roundView.group ?? 0}:${roundView.roundNumber}:${roundView.payloadChecksum}`}
          roundLabel={`${EVENT_TITLES[event]} · Season ${season} · ${roundLabel(roundView.group, roundView.roundNumber)}`}
          meta={`Rules v${roundView.rulesVersion} · checksum ${roundView.payloadChecksum.slice(0, 12)}… · Replay never consumes RNG and never mutates save state.`}
          saveId={saveId}
        />
      ) : (
        <Loading label="Loading round…" />
      )}

      {team && teams ? (
        <Card
          eyebrow={EVENT_TITLES[event]}
          title={
            teams.isFinal
              ? isTypeTournamentHistory && tournament && !tournament.isDirectFinal
                ? 'Team standings — Type Cup Final'
                : 'Team standings'
              : `Team standings so far — ${teams.groupsCompleted} of 4 groups`
          }
        >
          {isTypeTournamentHistory && tournament && !tournament.isDirectFinal ? (
            <p className="muted small">
              Final standings once the Cup completes; qualification-only teams keep their appearance
              on the{' '}
              <Link to={cupEditionPath(saveId, 'type', season)}>Type Cup edition page</Link> with
              per-group cutoffs.
            </p>
          ) : null}
          <div className="table-wrap">
            <table className="data-table">
              <thead>
                <tr>
                    <th scope="col" className="numeric">Rank</th>
                    <th scope="col">Team</th>
                    <th scope="col" className="numeric">
                      Points
                    </th>
                </tr>
              </thead>
              <tbody>
                {teams.teams.map((row) => (
                  <tr key={row.teamName}>
                    <td className="numeric">{row.rank ?? '—'}</td>
                    <td>{row.teamName}</td>
                    <td className="numeric">{formatPoints(row.scoreThousandths)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </Card>
      ) : null}
    </>
  );
}
