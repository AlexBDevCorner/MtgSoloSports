import { useEffect, useState } from 'react';
import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import { ApiError, apiErrorMessage } from '../../shared/api/http';
import {
  fetchHistoryCompetitions,
  fetchHistoryRoundReplay,
  fetchHistoryRounds,
  fetchHistorySeasonTable,
  fetchHistorySeasons,
  fetchHistoryStageStandings,
  fetchHistoryStages,
  type HistoryCompetitions,
  type HistoryRoundReplay,
  type HistoryRounds,
  type HistorySeasons,
  type HistorySeasonTable,
  type HistoryStages,
  type HistoryStageStandings,
} from './historyApi';
import {
  fetchColorCupIndividual,
  fetchColorCupTeam,
  type ColorCupIndividualResult,
  type ColorCupTeamResult,
} from '../cups/colorCupApi';
import { fetchTypeCupTeam, type TypeCupTeamResult } from '../cups/typeCupApi';
import { RoundReveal } from '../reveal/RoundReveal';
import { AthleteLink, Link } from '../routing/router';
import { cupsPath, savesPath, standingsLeaguePath } from '../routing/routes';

/** Display-only projection of a fixed-point thousandths value (no sporting math). */
function formatPoints(thousandths: number): string {
  return (thousandths / 1000).toFixed(3);
}

function useAsync<T>(
  key: string | null,
  load: (signal: AbortSignal) => Promise<T>,
): { data: T | null; loading: boolean; error: string | null } {
  const [data, setData] = useState<T | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!key) {
      setData(null);
      setLoading(false);
      setError(null);
      return;
    }
    const controller = new AbortController();
    const { signal } = controller;
    setLoading(true);
    setError(null);
    load(signal)
      .then((loaded) => {
        setData(loaded);
        setLoading(false);
      })
      .catch((failure: unknown) => {
        if (failure instanceof DOMException && failure.name === 'AbortError') {
          return;
        }
        if (failure instanceof ApiError && failure.status === 404) {
          setData(null);
          setError(null);
        } else {
          setError(apiErrorMessage(failure));
        }
        setLoading(false);
      });
    return () => {
      controller.abort();
    };
    // load is stable per key via inline closure over ids; re-run on key change only.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key]);

  return { data, loading, error };
}

export interface HistorySelection {
  season: number | null;
  competition: number | null;
  stage: number | null;
  round: number | null;
}

export function HistoryPage({
  saveId,
  urlSeason,
  urlCompetition,
  urlStage,
  urlRound,
  onHistoryChange,
}: {
  saveId: string;
  /** Shareable selections from `?season=&competition=&stage=&round=`; null means latest fallback. */
  urlSeason: number | null;
  urlCompetition: number | null;
  urlStage: number | null;
  urlRound: number | null;
  onHistoryChange: (selection: HistorySelection) => void;
}) {
  const [seasonNumber, setSeasonNumber] = useState<number | null>(urlSeason);
  const [leagueId, setLeagueId] = useState<number | null>(urlCompetition);
  const [stageNumber, setStageNumber] = useState<number | null>(urlStage);
  const [roundNumber, setRoundNumber] = useState<number | null>(urlRound);

  // Sync Back/Forward navigation of the shareable query into local selection.
  useEffect(() => {
    setSeasonNumber(urlSeason);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [urlSeason, saveId]);
  useEffect(() => {
    setLeagueId(urlCompetition);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [urlCompetition, saveId]);
  useEffect(() => {
    setStageNumber(urlStage);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [urlStage, saveId]);
  useEffect(() => {
    setRoundNumber(urlRound);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [urlRound, saveId]);

  const seasonsState = useAsync<HistorySeasons>(saveId, (signal) =>
    fetchHistorySeasons(saveId as string, signal),
  );
  const seasons = seasonsState.data?.seasons ?? [];

  useEffect(() => {
    if (seasons.length === 0) {
      return;
    }
    setSeasonNumber((current) => {
      // Explicit shareable `?season=` wins when it names a persisted season.
      if (urlSeason !== null && seasons.some((row) => row.seasonNumber === urlSeason)) {
        return urlSeason;
      }
      if (current !== null && seasons.some((row) => row.seasonNumber === current)) {
        return current;
      }
      return seasons[seasons.length - 1]!.seasonNumber;
    });
  }, [seasons, urlSeason]);

  const competitionsKey =
    saveId && seasonNumber !== null ? `${saveId}/s${seasonNumber}` : null;
  const competitionsState = useAsync<HistoryCompetitions>(competitionsKey, (signal) =>
    fetchHistoryCompetitions(saveId as string, seasonNumber as number, signal),
  );
  const competitions = competitionsState.data?.competitions ?? [];

  useEffect(() => {
    if (competitions.length === 0) {
      return;
    }
    setLeagueId((current) => {
      if (
        urlCompetition !== null &&
        competitions.some((row) => row.leagueId === urlCompetition)
      ) {
        return urlCompetition;
      }
      if (current !== null && competitions.some((row) => row.leagueId === current)) {
        return current;
      }
      return [...competitions].sort((a, b) => a.leagueId - b.leagueId)[0]!.leagueId;
    });
  }, [competitions, urlCompetition]);

  const stagesKey =
    saveId && seasonNumber !== null && leagueId !== null
      ? `${saveId}/s${seasonNumber}/l${leagueId}`
      : null;
  const stagesState = useAsync<HistoryStages>(stagesKey, (signal) =>
    fetchHistoryStages(saveId as string, seasonNumber as number, leagueId as number, signal),
  );
  const stages = stagesState.data?.stages ?? [];

  useEffect(() => {
    if (stages.length === 0) {
      return;
    }
    setStageNumber((current) => {
      if (urlStage !== null && stages.some((row) => row.stageNumber === urlStage)) {
        return urlStage;
      }
      if (current !== null && stages.some((row) => row.stageNumber === current)) {
        return current;
      }
      return stages[stages.length - 1]!.stageNumber;
    });
  }, [stages, urlStage]);

  const roundsKey =
    saveId && seasonNumber !== null && leagueId !== null && stageNumber !== null
      ? `${saveId}/s${seasonNumber}/l${leagueId}/st${stageNumber}`
      : null;
  const roundsState = useAsync<HistoryRounds>(roundsKey, (signal) =>
    fetchHistoryRounds(
      saveId as string,
      seasonNumber as number,
      leagueId as number,
      stageNumber as number,
      signal,
    ),
  );
  const roundSummaries = roundsState.data?.rounds ?? [];

  useEffect(() => {
    if (roundSummaries.length === 0) {
      return;
    }
    setRoundNumber((current) => {
      if (
        urlRound !== null &&
        roundSummaries.some((row) => row.roundNumber === urlRound)
      ) {
        return urlRound;
      }
      if (
        current !== null &&
        roundSummaries.some((row) => row.roundNumber === current)
      ) {
        return current;
      }
      return roundSummaries[roundSummaries.length - 1]!.roundNumber;
    });
  }, [roundSummaries, urlRound]);

  const replayKey =
    saveId && seasonNumber !== null && leagueId !== null && stageNumber !== null && roundNumber !== null
      ? `${saveId}/s${seasonNumber}/l${leagueId}/st${stageNumber}/r${roundNumber}`
      : null;
  const replayState = useAsync<HistoryRoundReplay>(replayKey, (signal) =>
    fetchHistoryRoundReplay(
      saveId as string,
      seasonNumber as number,
      leagueId as number,
      stageNumber as number,
      roundNumber as number,
      signal,
    ),
  );

  const standingsKey =
    saveId && seasonNumber !== null && leagueId !== null && stageNumber !== null
      ? `${saveId}/s${seasonNumber}/l${leagueId}/st${stageNumber}/standings`
      : null;
  const standingsState = useAsync<HistoryStageStandings>(standingsKey, (signal) =>
    fetchHistoryStageStandings(
      saveId as string,
      seasonNumber as number,
      leagueId as number,
      stageNumber as number,
      signal,
    ),
  );

  const tableKey =
    saveId && seasonNumber !== null && leagueId !== null
      ? `${saveId}/s${seasonNumber}/l${leagueId}/table`
      : null;
  const tableState = useAsync<HistorySeasonTable>(tableKey, (signal) =>
    fetchHistorySeasonTable(
      saveId as string,
      seasonNumber as number,
      leagueId as number,
      signal,
    ),
  );

  const colorIndividualKey =
    saveId && seasonNumber !== null ? `${saveId}/cup-color-ind/s${seasonNumber}` : null;
  const colorIndividualState = useAsync<ColorCupIndividualResult>(
    colorIndividualKey,
    (signal) => fetchColorCupIndividual(saveId as string, seasonNumber as number, signal),
  );

  const colorTeamKey =
    saveId && seasonNumber !== null ? `${saveId}/cup-color-team/s${seasonNumber}` : null;
  const colorTeamState = useAsync<ColorCupTeamResult>(colorTeamKey, (signal) =>
    fetchColorCupTeam(saveId as string, seasonNumber as number, signal),
  );

  const typeTeamKey =
    saveId && seasonNumber !== null ? `${saveId}/cup-type-team/s${seasonNumber}` : null;
  const typeTeamState = useAsync<TypeCupTeamResult>(typeTeamKey, (signal) =>
    fetchTypeCupTeam(saveId as string, seasonNumber as number, signal),
  );

  if (seasonsState.loading && seasons.length === 0) {
    return <Loading label="Loading history…" />;
  }

  if (seasonsState.error) {
    return (
      <Notice tone="error" title="History unavailable">
        <p>{seasonsState.error}</p>
        <p>
          <Link to={savesPath()} className="ghost-button">
            Back to saves
          </Link>
        </p>
      </Notice>
    );
  }

  if (seasons.length === 0) {
    return (
      <Notice tone="empty" title="No history yet">
        <p>This save has no persisted seasons to browse.</p>
      </Notice>
    );
  }

  const replay = replayState.data;
  const stageStandings = standingsState.data;
  const seasonTable = tableState.data;

  function pushSelection(next: HistorySelection): void {
    onHistoryChange(next);
  }

  return (
    <div className="dashboard">
      <Card
        eyebrow="History navigation"
        title="Season · competition · stage · round"
        action={
          seasonsState.loading ? <span className="muted small">Refreshing…</span> : undefined
        }
      >
        <div className="live-controls">
          <label className="field">
            <span>Season</span>
            <select
              value={seasonNumber ?? ''}
              onChange={(event) => {
                const next = Number.parseInt(event.target.value, 10);
                const season = Number.isNaN(next) ? null : next;
                setSeasonNumber(season);
                setLeagueId(null);
                setStageNumber(null);
                setRoundNumber(null);
                pushSelection({ season, competition: null, stage: null, round: null });
              }}
            >
              {seasons.map((row) => (
                <option key={row.seasonNumber} value={row.seasonNumber}>
                  Season {row.seasonNumber}
                  {row.isComplete ? '' : ' (in progress)'}
                </option>
              ))}
            </select>
          </label>
          <label className="field">
            <span>Competition</span>
            <select
              value={leagueId ?? ''}
              disabled={competitions.length === 0}
              onChange={(event) => {
                const next = Number.parseInt(event.target.value, 10);
                const competition = Number.isNaN(next) ? null : next;
                setLeagueId(competition);
                setStageNumber(null);
                setRoundNumber(null);
                pushSelection({ season: seasonNumber, competition, stage: null, round: null });
              }}
            >
              {competitions.map((row) => (
                <option key={row.leagueId} value={row.leagueId}>
                  {row.name}
                </option>
              ))}
            </select>
          </label>
          <label className="field">
            <span>Stage</span>
            <select
              value={stageNumber ?? ''}
              disabled={stages.length === 0}
              onChange={(event) => {
                const next = Number.parseInt(event.target.value, 10);
                const stage = Number.isNaN(next) ? null : next;
                setStageNumber(stage);
                setRoundNumber(null);
                pushSelection({ season: seasonNumber, competition: leagueId, stage, round: null });
              }}
            >
              {stages.map((row) => (
                <option key={row.stageNumber} value={row.stageNumber}>
                  Stage {row.stageNumber}
                  {row.isComplete ? '' : ` (${row.completedRounds}/16)`}
                </option>
              ))}
            </select>
          </label>
          <label className="field">
            <span>Round</span>
            <select
              value={roundNumber ?? ''}
              disabled={roundSummaries.length === 0}
              onChange={(event) => {
                const next = Number.parseInt(event.target.value, 10);
                const round = Number.isNaN(next) ? null : next;
                setRoundNumber(round);
                pushSelection({ season: seasonNumber, competition: leagueId, stage: stageNumber, round });
              }}
            >
              {roundSummaries.map((row) => (
                <option key={row.roundNumber} value={row.roundNumber}>
                  Round {row.roundNumber}
                </option>
              ))}
            </select>
          </label>
        </div>
        <p className="muted small">
          Season, competition and stage lists plus round summaries come from normalized
          tables only; the exact round payload is decompressed only when that round is
          requested. Replay never consumes RNG and never mutates save state. The
          current selection is reflected in the URL (`?season=&amp;competition=&amp;stage=&amp;round=`)
          so it can be copied or opened in another tab.
        </p>
        {competitionsState.error ? (
          <p className="muted small">Competitions: {competitionsState.error}</p>
        ) : null}
        {stagesState.error ? <p className="muted small">Stages: {stagesState.error}</p> : null}
        {roundsState.error ? <p className="muted small">Rounds: {roundsState.error}</p> : null}
      </Card>

      <Card
        eyebrow="Post-season Cup"
        title={
          seasonNumber !== null
            ? seasonNumber % 2 === 1
              ? `Season ${seasonNumber} · Color Cup`
              : `Season ${seasonNumber} · Type Cup`
            : 'Post-season Cup'
        }
      >
        {seasonNumber === null ? (
          <p className="muted">Select a season to see its post-season Cup outcome.</p>
        ) : seasonNumber % 2 === 1 ? (
          <>
            <p className="muted small">
              Odd seasons run the Color Cup after feeder rebalancing and before bonus
              aging: 32 selected athletes in a 16-round individual plus four 8-round
              rank groups for the team title. Honours and stories persist with the Cup.
            </p>
            {colorIndividualState.loading && !colorIndividualState.data ? (
              <Loading label="Loading Color Cup…" />
            ) : colorIndividualState.data ? (
              <p>
                Individual champion{' '}
                <AthleteLink
                  saveId={saveId}
                  athleteId={colorIndividualState.data!.championAthleteId}
                  name={colorIndividualState.data!.championName}
                />{' '}
                · {colorIndividualState.data!.cupSize} athletes ·{' '}
                {colorIndividualState.data!.rounds} rounds.
              </p>
            ) : (
              <p className="muted">No Color Cup individual result for this season yet.</p>
            )}
            {colorTeamState.loading && !colorTeamState.data ? (
              <Loading label="Loading Color Cup team…" />
            ) : colorTeamState.data ? (
              <p>
                Team champion {colorTeamState.data!.championTeamName} ·{' '}
                {colorTeamState.data!.teamCount} teams · {colorTeamState.data!.groupCount}{' '}
                groups × {colorTeamState.data!.groupRounds} rounds.
              </p>
            ) : (
              <p className="muted">No Color Cup team result for this season yet.</p>
            )}
            <p className="muted small">
              Open the <Link to={cupsPath(saveId)} className="card-name card-link">Cups tab</Link> for
              the full field, standings and replay payloads.
            </p>
          </>
        ) : (
          <>
            <p className="muted small">
              Even seasons run the team-only Type Cup after feeder rebalancing and before
              bonus aging. Permanent nationality is set on participation. Honours and
              stories persist with the Cup.
            </p>
            {typeTeamState.loading && !typeTeamState.data ? (
              <Loading label="Loading Type Cup…" />
            ) : typeTeamState.data ? (
              <p>
                Team champion {typeTeamState.data!.championTeamName} ·{' '}
                {typeTeamState.data!.teamCount} teams · {typeTeamState.data!.groupCount}{' '}
                groups × {typeTeamState.data!.groupRounds} rounds.
              </p>
            ) : (
              <p className="muted">No Type Cup team result for this season yet.</p>
            )}
            <p className="muted small">
              Open the <Link to={cupsPath(saveId)} className="card-name card-link">Cups tab</Link> for
              the full allocation, standings and replay payloads.
            </p>
          </>
        )}
      </Card>

      {replayState.loading && !replay ? (
        <Card eyebrow="Exact replay" title="Exact replay">
          <Loading label="Loading replay…" />
        </Card>
      ) : replayState.error ? (
        <Card eyebrow="Exact replay" title="Exact replay">
          <Notice tone="error" title="Replay unavailable">
            <p>{replayState.error}</p>
          </Notice>
        </Card>
      ) : !replay || replay.placements.length === 0 ? (
        <Card eyebrow="Exact replay" title="Exact replay">
          <Notice tone="empty" title="No round selected">
            <p>Simulate rounds on the Live tab, then pick a persisted round here.</p>
          </Notice>
        </Card>
      ) : (
        <RoundReveal
          placements={replay.placements}
          revealKey={`${replay.seasonNumber}:${replay.leagueId}:${replay.stageNumber}:${replay.roundNumber}:${replay.payloadChecksum}`}
          roundLabel={`Season ${replay.seasonNumber} · ${replay.leagueName} · Stage ${replay.stageNumber} · Round ${replay.roundNumber}`}
          meta={`Rules v${replay.rulesVersion} · checksum ${replay.payloadChecksum.length > 12 ? `${replay.payloadChecksum.slice(0, 12)}…` : replay.payloadChecksum} · same presentation model as live results. Replay never consumes RNG and never mutates save state.`}
          saveId={saveId}
        />
      )}

      <Card
        eyebrow="Standings"
        title={
          stageStandings && stageStandings.standings.length > 0
            ? `${stageStandings.leagueName} — Stage ${stageStandings.stageNumber} standings`
            : 'Stage standings'
        }
      >
        {standingsState.loading && !stageStandings ? (
          <Loading label="Loading stage standings…" />
        ) : standingsState.error ? (
          <Notice tone="error" title="Stage standings unavailable">
            <p>{standingsState.error}</p>
          </Notice>
        ) : !stageStandings || stageStandings.standings.length === 0 ? (
          <Notice tone="empty" title="No stage standings yet">
            <p>Complete the stage on the Live tab to persist normalized standings.</p>
          </Notice>
        ) : (
          <div className="table-wrap">
            <table className="data-table">
              <thead>
                <tr>
                  <th scope="col">Rank</th>
                  <th scope="col">Card</th>
                  <th scope="col">Stage score</th>
                  <th scope="col">Champ pts</th>
                  <th scope="col">Round W</th>
                </tr>
              </thead>
              <tbody>
                {stageStandings.standings.map((row) => (
                  <tr key={row.athleteId}>
                    <td className="numeric">{row.stageRank}</td>
                    <td>
                      <AthleteLink saveId={saveId} athleteId={row.athleteId} name={row.name} />
                    </td>
                    <td className="numeric">{formatPoints(row.stageScoreThousandths)}</td>
                    <td className="numeric">
                      {formatPoints(row.championshipPointsThousandths)}
                    </td>
                    <td className="numeric">{row.roundWins}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>

      <Card
        eyebrow="Season table"
        title={
          seasonTable && seasonTable.standings.length > 0
            ? `${seasonTable.leagueName} — Season ${seasonTable.seasonNumber} final`
            : 'Season final table'
        }
        action={
          seasonNumber !== null && leagueId !== null ? (
            <Link
              to={standingsLeaguePath(saveId, leagueId, { season: seasonNumber, view: 'matrix' })}
              className="ghost-button"
              title="Open this league in the dedicated standings matrix"
            >
              Open matrix
            </Link>
          ) : undefined
        }
      >
        {tableState.loading && !seasonTable ? (
          <Loading label="Loading season table…" />
        ) : tableState.error ? (
          <Notice tone="error" title="Season table unavailable">
            <p>{tableState.error}</p>
          </Notice>
        ) : !seasonTable || seasonTable.standings.length === 0 ? (
          <Notice tone="empty" title="No final table yet">
            <p>
              Season final tables persist only when all 32 stages are complete for every
              active league.
            </p>
          </Notice>
        ) : (
          <div className="table-wrap">
            <table className="data-table">
              <thead>
                <tr>
                  <th scope="col">Rank</th>
                  <th scope="col">Card</th>
                  <th scope="col">Champ pts</th>
                  <th scope="col">Stage W</th>
                  <th scope="col">Round W</th>
                </tr>
              </thead>
              <tbody>
                {seasonTable.standings.map((row) => (
                  <tr key={row.athleteId}>
                    <td className="numeric">{row.seasonRank}</td>
                    <td>
                      <AthleteLink saveId={saveId} athleteId={row.athleteId} name={row.name} />
                      {row.isChampion ? <span className="card-sub"> · Champion</span> : null}
                    </td>
                    <td className="numeric">
                      {formatPoints(row.totalChampionshipPointsThousandths)}
                    </td>
                    <td className="numeric">{row.stageWins}</td>
                    <td className="numeric">{row.roundWins}</td>
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
