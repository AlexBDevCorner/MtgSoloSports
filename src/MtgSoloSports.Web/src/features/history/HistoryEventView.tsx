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
  onChange,
}: {
  saveId: string;
  season: number;
  event: EventKey;
  urlGroup: number | null;
  urlRound: number | null;
  onChange: (group: number | null, round: number) => void;
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

  const selected: PlayedRound | null = useMemo(() => {
    if (!rounds || rounds.length === 0) {
      return null;
    }
    const wantedGroup = team ? urlGroup : null;
    const match = urlRound !== null ? rounds.find((r) => r.round === urlRound && r.group === wantedGroup) : undefined;
    return match ?? rounds[rounds.length - 1]!;
  }, [rounds, urlRound, urlGroup, team]);

  useEffect(() => {
    if (!selected) {
      setRoundView(null);
      return;
    }
    const controller = new AbortController();
    fetchEventRound(saveId, season, event, selected.round, selected.group, controller.signal)
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
  const groupRounds = rounds.filter((r) => r.group === visibleGroup);

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
        {team ? (
          <label className="field">
            <span>{isTypeTournamentHistory && tournament && !tournament.isDirectFinal ? 'Squad rank group' : 'Group'}</span>
            <select
              value={visibleGroup ?? ''}
              disabled={groups.length === 0}
              onChange={(changed) => {
                const group = Number.parseInt(changed.target.value, 10);
                const last = rounds.filter((r) => r.group === group).at(-1);
                if (last) {
                  onChange(group, last.round);
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
                onChange(visibleGroup, round);
              }
            }}
          >
            {groupRounds.map((r) => (
              <option key={`${r.group ?? 0}-${r.round}`} value={r.round}>
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
          revealKey={`${event}:${season}:${roundView.group ?? 0}:${roundView.roundNumber}:${roundView.payloadChecksum}`}
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
