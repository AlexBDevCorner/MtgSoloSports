import { useCallback, useEffect, useState } from 'react';
import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import { ApiError, apiErrorMessage } from '../../shared/api/http';
import { AthleteLink } from '../routing/router';
import {
  fetchTypeCupTeam,
  type TypeCupTeamResult,
} from './typeCupApi';
import { fetchSeasonStatus, type SeasonStatus } from '../dashboard/dashboardApi';
import { EventLiveAction } from './EventLiveAction';

/** Display-only projection of fixed-point thousandths (no sporting math). */
function formatPoints(thousandths: number): string {
  return (thousandths / 1000).toFixed(3);
}

function medalBadge(medal: string): string {
  if (medal === 'Gold') {
    return '🥇 Gold';
  }
  if (medal === 'Silver') {
    return '🥈 Silver';
  }
  if (medal === 'Bronze') {
    return '🥉 Bronze';
  }
  return '—';
}

export function TypeCupPage({ saveId }: { saveId: string }) {
  const [team, setTeam] = useState<TypeCupTeamResult | null>(null);
  const [teamLoading, setTeamLoading] = useState(false);
  const [status, setStatus] = useState<SeasonStatus | null>(null);
  const [teamError, setTeamError] = useState<string | null>(null);
  const [teamNotFound, setTeamNotFound] = useState(false);

  const loadTeam = useCallback(
    async (signal: AbortSignal) => {
      if (!saveId) {
        return;
      }
      setTeamLoading(true);
      setTeamError(null);
      try {
        const loaded = await fetchTypeCupTeam(saveId, null, signal);
        setTeam(loaded);
        setTeamNotFound(false);
      } catch (failure: unknown) {
        if (failure instanceof DOMException && failure.name === 'AbortError') {
          return;
        }
        if (failure instanceof ApiError && failure.status === 404) {
          setTeam(null);
          setTeamNotFound(true);
        } else {
          setTeamError(apiErrorMessage(failure));
        }
      } finally {
        setTeamLoading(false);
      }
    },
    [saveId],
  );

  useEffect(() => {
    if (!saveId) {
      setTeam(null);
      setTeamLoading(false);
      setTeamError(null);
      setTeamNotFound(false);
      return;
    }
    const controller = new AbortController();
    void loadTeam(controller.signal);
    fetchSeasonStatus(saveId, controller.signal)
      .then(setStatus)
      .catch(() => setStatus(null));
    return () => {
      controller.abort();
    };
  }, [saveId, loadTeam]);

  if (teamLoading && !team) {
    return <Loading label="Loading Type Cup…" />;
  }

  return (
    <div className="page-grid">
      <Card
        eyebrow="Type Cup · team"
        title={
          team
            ? `Season ${team.sourceSeasonNumber} — ${team.championTeamName} wins the Cup`
            : 'Type Cup team event'
        }
        action={
          teamLoading ? <span className="muted small">Refreshing…</span> : undefined
        }
        info={
          <p>
            Runs four rank groups (#1 vs #1 through #4 vs #4) with 8 rounds each for every
            allocated creature-type team. Team score is the sum of the four legs; exact group
            payloads persist for replay and career bonus never changes.
          </p>
        }
      >
        {teamError ? (
          <Notice tone="error" title="Type Cup team unavailable">
            <p>{teamError}</p>
          </Notice>
        ) : null}
        {team ? (
          <>
            <p className="muted small">
              {team.teamCount} creature-type teams · 4 rank groups ×{' '}
              {team.groupRounds} rounds · checksum{' '}
              <code title={team.checksum}>
                {team.checksum.length > 12
                  ? `${team.checksum.slice(0, 12)}…`
                  : team.checksum}
              </code>{' '}
              · active bonus applies, no new bonus or league points. Uncapped
              participants are capped to their creature type with the event.
            </p>
            <div className="table-wrap">
              <table className="data-table">
                <thead>
                  <tr>
                    <th scope="col">Medal</th>
                    <th scope="col">Rank</th>
                    <th scope="col">Team</th>
                    <th scope="col">Score</th>
                  </tr>
                </thead>
                <tbody>
                  {team.teams.map((entry) => (
                    <tr key={entry.creatureType}>
                      <td>{medalBadge(entry.medal)}</td>
                      <td className="numeric">{entry.teamRank}</td>
                      <td>{entry.teamName}</td>
                      <td className="numeric">
                        {formatPoints(entry.teamScoreThousandths)}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </>
        ) : teamNotFound ? (
          <p className="muted">
            No Type Cup team result yet. Resolve the Type Cup allocation for a
            completed even season first, then run the four 8-round rank groups.
          </p>
        ) : null}
        {team ? null : <EventLiveAction saveId={saveId} eventKey="type-cup-team" status={status} />}
      </Card>

      <Card
        eyebrow="Legs"
        title={team ? `Group legs — ${team.legs.length} athletes` : 'Group legs'}
      >
        {!team ? (
          <p className="muted">Run the team event to populate group legs.</p>
        ) : (
          <div className="table-wrap">
            <table className="data-table">
              <thead>
                <tr>
                  <th scope="col">Group</th>
                  <th scope="col">Rank</th>
                  <th scope="col">Card</th>
                  <th scope="col">Leg score</th>
                </tr>
              </thead>
              <tbody>
                {team.legs.map((leg) => (
                  <tr key={leg.athleteId}>
                    <td className="numeric">#{leg.groupNumber}</td>
                    <td className="numeric">{leg.groupRank}</td>
                    <td>
                      <AthleteLink saveId={saveId} athleteId={leg.athleteId} name={leg.name} />
                      <span className="card-sub">
                        {' '}
                        · {leg.creatureType} · #{leg.selectionRank}
                      </span>
                    </td>
                    <td className="numeric">
                      {formatPoints(leg.groupScoreThousandths)}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>
    </div>
  );
}
