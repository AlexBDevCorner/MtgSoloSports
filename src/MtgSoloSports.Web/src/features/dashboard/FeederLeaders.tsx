import { useEffect, useMemo, useState } from 'react';
import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import { ApiError, apiErrorMessage } from '../../shared/api/http';
import { groupLeaguesByTier } from '../../shared/leagueTiers';
import { fetchCurrentStandings, type CurrentStandingRow } from '../athletes/athleteApi';
import { AthleteLink, Link } from '../routing/router';
import { livePath, standingsLeaguePath } from '../routing/routes';
import type { SeasonProgressLeague } from './dashboardApi';

function formatPoints(thousandths: number): string {
  return (thousandths / 1000).toFixed(3);
}

interface DivisionOption {
  key: string;
  label: string;
  leagueIds: number[];
}

interface DivisionLeader {
  leagueId: number;
  leagueName: string;
  leader: CurrentStandingRow | null;
}

/**
 * MSS-060 on-demand feeder leaders. The pyramid holds up to 24 feeder
 * leagues; loading all detailed standings eagerly would fire dozens of
 * requests when only summaries are needed. Division tabs (Feeder 1 / 2 / 3,
 * or the legacy single Feeder) fetch only the selected division's eight
 * leagues, one leader per league, with links into Standings and Live.
 */
export function FeederLeaders({
  saveId,
  leagues,
}: {
  saveId: string;
  leagues: SeasonProgressLeague[];
}) {
  const divisions = useMemo<DivisionOption[]>(() => {
    const groups = groupLeaguesByTier(
      leagues.map((league) => ({
        leagueId: league.leagueId,
        name: league.leagueName,
        kind: league.leagueKind,
        feederDivision: league.feederDivision ?? null,
        leagueLevel: league.leagueLevel ?? null,
      })),
    );
    const options: DivisionOption[] = [];
    if (groups.feeder1.length > 0) {
      options.push({ key: 'f1', label: 'Feeder 1', leagueIds: groups.feeder1.map((row) => row.leagueId) });
    }
    if (groups.feeder2.length > 0) {
      options.push({ key: 'f2', label: 'Feeder 2', leagueIds: groups.feeder2.map((row) => row.leagueId) });
    }
    if (groups.feeder3.length > 0) {
      options.push({ key: 'f3', label: 'Feeder 3', leagueIds: groups.feeder3.map((row) => row.leagueId) });
    }
    if (groups.legacyFeeder.length > 0) {
      options.push({ key: 'legacy', label: 'Feeder', leagueIds: groups.legacyFeeder.map((row) => row.leagueId) });
    }
    return options;
  }, [leagues]);

  const [selected, setSelected] = useState<string | null>(null);
  const [leaders, setLeaders] = useState<DivisionLeader[] | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const active = divisions.find((option) => option.key === selected) ?? divisions[0] ?? null;

  useEffect(() => {
    if (!active) {
      setLeaders(null);
      setLoading(false);
      setError(null);
      return;
    }
    const controller = new AbortController();
    const { signal } = controller;
    setLoading(true);
    setError(null);
    const names = new Map(leagues.map((league) => [league.leagueId, league.leagueName]));
    Promise.all(
      active.leagueIds.map(async (leagueId): Promise<DivisionLeader> => {
        try {
          const standings = await fetchCurrentStandings(saveId, leagueId, signal);
          const top = [...standings.standings].sort((a, b) => a.seasonRank - b.seasonRank)[0] ?? null;
          return { leagueId, leagueName: standings.leagueName || names.get(leagueId) || `League ${leagueId}`, leader: top };
        } catch (failure) {
          if (failure instanceof DOMException && failure.name === 'AbortError') {
            throw failure;
          }
          if (failure instanceof ApiError && failure.status === 404) {
            return { leagueId, leagueName: names.get(leagueId) || `League ${leagueId}`, leader: null };
          }
          throw failure;
        }
      }),
    )
      .then((loaded) => {
        setLeaders(loaded);
        setLoading(false);
      })
      .catch((failure: unknown) => {
        if (failure instanceof DOMException && failure.name === 'AbortError') {
          return;
        }
        setError(apiErrorMessage(failure));
        setLeaders(null);
        setLoading(false);
      });
    return () => {
      controller.abort();
    };
  }, [saveId, active, leagues]);

  return (
    <Card
      eyebrow="Leaders"
      title={`Feeder leagues — current leaders${active ? ` · ${active.label}` : ''}`}
      action={
        <Link to={livePath(saveId)} className="ghost-button">
          Open live
        </Link>
      }
      info={
        <p>
          Feeder champions are auto-promoted; qualifier bands differ per tier (Feeder 1:
          2–4 challenge upward, 17–24 defend; Feeder 2: 9–16 challenge, 17–24 defend;
          Feeder 3: 9–16 challenge). Zones are visual only and never change selection math.
          Only the selected division loads detailed standings.
        </p>
      }
    >
      {divisions.length === 0 ? (
        <p className="muted">No feeder leagues in this season.</p>
      ) : (
        <>
          {divisions.length > 1 ? (
            <div className="segmented" role="group" aria-label="Feeder divisions">
              {divisions.map((option) => (
                <button
                  key={option.key}
                  type="button"
                  className={option.key === active?.key ? 'nav-item current' : 'nav-item'}
                  aria-pressed={option.key === active?.key}
                  onClick={() => {
                    setSelected(option.key);
                  }}
                >
                  {option.label}
                </button>
              ))}
            </div>
          ) : null}
          {loading && !leaders ? (
            <Loading label={`Loading ${active?.label ?? ''} leaders…`} />
          ) : error ? (
            <Notice tone="error" title="Leaders unavailable">
              <p>{error}</p>
            </Notice>
          ) : !leaders || leaders.length === 0 ? (
            <p className="muted">No completed stages yet; leaders appear after stage results persist.</p>
          ) : (
            <div className="table-wrap">
              <table className="data-table">
                <thead>
                  <tr>
                    <th scope="col">League</th>
                    <th scope="col">Leader</th>
                    <th scope="col" className="numeric">Champ pts</th>
                  </tr>
                </thead>
                <tbody>
                  {leaders.map((board) => (
                    <tr key={board.leagueId}>
                      <td>
                        <Link to={standingsLeaguePath(saveId, board.leagueId)}>{board.leagueName}</Link>
                      </td>
                      <td>
                        {board.leader ? (
                          <AthleteLink saveId={saveId} athleteId={board.leader.athleteId} name={board.leader.name} />
                        ) : (
                          <span className="muted">—</span>
                        )}
                      </td>
                      <td className="numeric">
                        {board.leader ? formatPoints(board.leader.totalChampionshipPointsThousandths) : '—'}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </>
      )}
    </Card>
  );
}
