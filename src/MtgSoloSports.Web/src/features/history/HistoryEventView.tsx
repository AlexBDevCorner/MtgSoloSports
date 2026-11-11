import { Fragment, useEffect, useMemo, useState } from 'react';
import { ApiError, apiErrorMessage } from '../../shared/api/http';
import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import { optionalTournament } from '../cups/typeCupTournamentApi';
import type { TypeCupTournament } from '../cups/typeCupTournamentApi';
import { fetchColorCupIndividualProgress, type ColorCupIndividualProgress } from '../cups/colorCupApi';
import { advancingLabel, cutoffSummary } from '../cups/colorCupProgress';
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
import { TeamBadge } from '../cups/TeamBadge';
import { cupTeamKey } from '../cups/cupTeamKeys';

/** Display-only projection of a fixed-point thousandths value (no sporting math). */
function formatPoints(thousandths: number): string {
  return (thousandths / 1000).toFixed(3);
}

/**
 * MSS-139 Cup team link for team standings rows. The event identity tells the
 * Cup (color vs type); the persisted team name is the team identity. Unknown
 * or empty names render as plain text, never a guessed URL.
 */
function CupTeamCell({
  saveId,
  event,
  teamName,
}: {
  saveId: string;
  event: EventKey;
  teamName: string;
}) {
  const cup = event === 'color-cup-team' ? 'color' : event === 'type-cup-team' ? 'type' : null;
  const key = cup ? cupTeamKey(cup, teamName) : null;
  if (!cup || !key) {
    return <>{teamName}</>;
  }
  return <TeamBadge saveId={saveId} cup={cup} teamKey={key} teamName={teamName} />;
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
  const [individualProgress, setIndividualProgress] = useState<ColorCupIndividualProgress | null>(null);
  const [error, setError] = useState<string | null>(null);
  const team = isTeamEvent(event);
  const isTypeTournamentHistory = event === 'type-cup-team';
  const isIndividualHistory = event === 'color-cup-individual';

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
    if (isIndividualHistory) {
      fetchColorCupIndividualProgress(saveId, season, controller.signal)
        .then(setIndividualProgress)
        .catch(() => setIndividualProgress(null));
    } else {
      setIndividualProgress(null);
    }
    return () => controller.abort();
  }, [saveId, season, event, team, isTypeTournamentHistory, isIndividualHistory]);

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
      if (isTypeCup && urlStage) {
        const staged = rounds.find(
          (r) =>
            r.round === urlRound &&
            r.group === wantedGroup &&
            (r.tournamentPhase ?? null) === urlStage.tournamentPhase &&
            (r.qualificationGroup ?? null) === urlStage.qualificationGroup,
        );
        if (staged) {
          return staged;
        }
        // MSS-079 correction: an explicit stage+round that was never persisted
        // must stay missing so the fetch surfaces a 404. Never fall back to a
        // different stage (e.g. Final) for an explicit stage URL.
        return {
          group: wantedGroup,
          round: urlRound,
          tournamentPhase: urlStage.tournamentPhase,
          qualificationGroup: urlStage.qualificationGroup,
        };
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
          // MSS-079 correction: an explicit but unpersisted stage+round must not
          // keep showing the previous stage's replay alongside the error.
          if (failure instanceof ApiError && failure.status === 404) {
            setRoundView(null);
          }
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
                if (Number.isNaN(group)) {
                  return;
                }
                // MSS-079 correction: keep the chosen qualification stage when the
                // squad-rank group changes. groupRounds is already scoped to the
                // previous group, so filter the full list by the new group AND the
                // selected stage. If that stage+group was never persisted, preserve
                // the requested stage so the fetch surfaces a missing replay
                // instead of jumping to the Final.
                const scoped =
                  isTypeCup && selectedStage
                    ? rounds.filter(
                        (r) =>
                          r.group === group &&
                          (r.tournamentPhase ?? null) === selectedStage.tournamentPhase &&
                          (r.qualificationGroup ?? null) === selectedStage.qualificationGroup,
                      )
                    : rounds.filter((r) => r.group === group);
                const last = scoped.at(-1);
                if (last) {
                  const stage =
                    last.tournamentPhase !== null &&
                    last.tournamentPhase !== undefined &&
                    last.qualificationGroup !== null &&
                    last.qualificationGroup !== undefined
                      ? { tournamentPhase: last.tournamentPhase, qualificationGroup: last.qualificationGroup }
                      : (selectedStage ?? null);
                  onChange(group, last.round, stage);
                } else {
                  onChange(group, selected?.round ?? 1, selectedStage ?? null);
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
                    <td>
                      <CupTeamCell saveId={saveId} event={event} teamName={row.teamName} />
                    </td>
                    <td className="numeric">{formatPoints(row.scoreThousandths)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </Card>
      ) : null}

      {isIndividualHistory && individualProgress ? (
        <Card
          eyebrow={EVENT_TITLES[event]}
          title={
            individualProgress.isPhased
              ? individualProgress.isComplete
                ? 'Final standings — 1–16 finalists, 17–32 eliminated'
                : `Progress — ${individualProgress.roundsPlayed} / ${individualProgress.totalRounds} · top 16 advance`
              : `Individual standings — ${individualProgress.roundsPlayed} / ${individualProgress.totalRounds}`
          }
        >
          {individualProgress.isPhased ? (
            <>
              <p className="muted small">
                {individualProgress.isComplete
                  ? `Phased 32-to-16 with full carryover. ${cutoffSummary(individualProgress.cutoffRank, individualProgress.tieDrawConsumed, false)}. Finalists carry 100% of qualifying points.`
                  : individualProgress.roundsPlayed <= individualProgress.qualifyingRounds
                    ? `Qualifying of 32 · ${cutoffSummary(individualProgress.cutoffRank, individualProgress.tieDrawConsumed, individualProgress.isProvisionalCutoff)}.`
                    : `Final of 16 active · cumulative scores · ${cutoffSummary(individualProgress.cutoffRank, individualProgress.tieDrawConsumed, false)}. Carryover intact (no reset).`}
              </p>
              <div className="table-wrap">
                <table className="data-table">
                  <thead>
                    <tr>
                      <th scope="col" className="numeric">Rank</th>
                      <th scope="col">Card</th>
                      <th scope="col" className="numeric">Points</th>
                      <th scope="col">Status</th>
                    </tr>
                  </thead>
                  <tbody>
                    {individualProgress.standings.map((row, index) => {
                      const showCut =
                        individualProgress.cutoffRank !== null &&
                        index === (individualProgress.cutoffRank ?? 16) - 1 &&
                        individualProgress.standings.length > (individualProgress.cutoffRank ?? 16);
                      return (
                        <Fragment key={row.athleteId}>
                          <tr>
                            <td className="numeric">{row.provisionalRank}</td>
                            <td>{row.name}</td>
                            <td className="numeric">{formatPoints(row.scoreThousandths)}</td>
                            <td>{advancingLabel(row.advancingStatus)}</td>
                          </tr>
                          {showCut ? (
                            <tr className="live-cut-line" aria-hidden="true">
                              <td colSpan={4} className="live-cut-cell">
                                <span className="live-cut-label">Cut — top {individualProgress.cutoffRank} advance</span>
                              </td>
                            </tr>
                          ) : null}
                        </Fragment>
                      );
                    })}
                  </tbody>
                </table>
              </div>
              {individualProgress.carryover && individualProgress.carryover.length > 0 ? (
                <p className="muted small">
                  Round-16 to round-17 continuity: {individualProgress.carryover.filter((c) => c.matches).length}/
                  {individualProgress.carryover.length} finalists carry 100% (before equals after).
                </p>
              ) : null}
              <p className="muted small">
                Every recorded round stays replayable above via the Round selector (
                {individualProgress.totalRounds} rounds for phased, 16 for legacy).
              </p>
            </>
          ) : (
            <>
              <p className="muted small">
                Legacy 16-round edition · {individualProgress.roundsPlayed} / {individualProgress.totalRounds} · no cutoff.
              </p>
              <div className="table-wrap">
                <table className="data-table">
                  <thead>
                    <tr>
                      <th scope="col" className="numeric">Rank</th>
                      <th scope="col">Card</th>
                      <th scope="col" className="numeric">Points</th>
                    </tr>
                  </thead>
                  <tbody>
                    {individualProgress.standings.map((row) => (
                      <tr key={row.athleteId}>
                        <td className="numeric">{row.provisionalRank}</td>
                        <td>{row.name}</td>
                        <td className="numeric">{formatPoints(row.scoreThousandths)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </>
          )}
        </Card>
      ) : null}
    </>
  );
}
