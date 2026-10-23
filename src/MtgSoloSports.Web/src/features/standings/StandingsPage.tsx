import { useEffect, useMemo, useRef, useState } from 'react';
import { Card } from '../../shared/ui/Card';
import { InfoDisclosure } from '../../shared/ui/InfoDisclosure';
import { Loading, Notice } from '../../shared/ui/Notice';
import { ApiError, apiErrorMessage } from '../../shared/api/http';
import {
  TIER_BONUS_SCALE_LINE,
  groupLeaguesByTier,
  leagueLevelLabel,
  tierBonusLabel,
} from '../../shared/leagueTiers';
import { fetchCurrentStandings, type CurrentStandings } from '../athletes/athleteApi';
import {
  fetchHistoryCompetitions,
  fetchHistorySeasonTable,
  fetchHistorySeasons,
  fetchHistoryStages,
  type HistoryCompetitions,
  type HistorySeasons,
  type HistorySeasonTable,
  type HistoryStages,
} from '../history/historyApi';
import { AthleteLink, Link } from '../routing/router';
import { historyPath, livePath, savesPath, type StandingsView } from '../routing/routes';
import { fetchSeasonPlacements, type SeasonPlacements } from './standingsApi';
import {
  allStageColumns,
  buildCellMap,
  cellTitle,
  formatBonus,
  formatPoints,
  maxCompletedStage,
  placeLabel,
  podiumClass,
  sortMatrixRows,
  visibleStageColumns,
  type CombinedMatrixRow,
  type MatrixSortKey,
  type StageWindow,
} from './matrix';
import './StandingsPage.css';

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
        // Ignore out-of-order resolves after a rapid key change; the cleanup
        // aborts the previous request but an already-resolved fetch still runs.
        if (signal.aborted) {
          return;
        }
        setData(loaded);
        setLoading(false);
      })
      .catch((failure: unknown) => {
        if (signal.aborted) {
          return;
        }
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

export interface StandingsSelection {
  league: number | null;
  season: number | null;
  view: StandingsView | null;
}

interface LeagueRef {
  leagueId: number;
  kind: string;
  leagueLevel?: string | null;
}

/**
 * MSS-084: deterministic default keeps Superleague first. Falls back to the
 * smallest league id only when no Superleague exists (legacy single-feeder
 * saves). Never parses names; kind/level are canonical backend identities.
 */
export function defaultLeagueIdFor(competitions: readonly LeagueRef[]): number | null {
  if (competitions.length === 0) {
    return null;
  }
  const superleague = competitions
    .filter((row) => row.kind === 'Superleague' || row.leagueLevel === 'Superleague')
    .sort((a, b) => a.leagueId - b.leagueId);
  if (superleague.length > 0) {
    return superleague[0]!.leagueId;
  }
  return [...competitions].sort((a, b) => a.leagueId - b.leagueId)[0]!.leagueId;
}

interface TotalsRow {
  athleteId: number;
  name: string;
  seasonRank: number;
  totalChampionshipPointsThousandths: number;
  totalStageScoreThousandths: number;
  roundWins: number;
  stageWins: number;
  isChampion: boolean;
}

export function StandingsPage({
  saveId,
  urlLeagueId,
  urlSeason,
  urlView,
  onStandingsChange,
}: {
  saveId: string;
  urlLeagueId: number | null;
  urlSeason: number | null;
  urlView: StandingsView | null;
  onStandingsChange: (selection: StandingsSelection, options?: { replace?: boolean }) => void;
}) {
  const [seasonNumber, setSeasonNumber] = useState<number | null>(urlSeason);
  const [leagueId, setLeagueId] = useState<number | null>(urlLeagueId);
  const [view, setView] = useState<StandingsView>(urlView ?? 'season');
  const [sortKey, setSortKey] = useState<MatrixSortKey>('rank');
  const [showBonus, setShowBonus] = useState(false);
  const [stageWindow, setStageWindow] = useState<StageWindow>('all');

  useEffect(() => {
    setSeasonNumber(urlSeason);
  }, [urlSeason, saveId]);
  // MSS-084: preserve the selected league across seasons via the shareable
  // URL. Season switches push the stable league id so standings and URL
  // agree. The URL stays authoritative: a league-less URL (fresh open or
  // Back to `/standings?season=N`) always resets local state so the same URL
  // renders the same default Superleague before and after reload. An in-app
  // switch without a router update yet is tracked via pendingPreserveRef and
  // handled in the competitions effect below.
  const saveIdRef = useRef(saveId);
  // Tracks an in-app season switch that optimistically preserved the league
  // id in the URL. When the preserved id is missing in the destination
  // season, the correction effect below falls back to Superleague/first and
  // rewrites the URL instead of showing the explicit invalid-league error
  // (which stays reserved for directly entered/shared invalid URLs).
  const pendingPreserveRef = useRef<{ season: number; league: number | null } | null>(null);
  useEffect(() => {
    if (saveIdRef.current !== saveId) {
      saveIdRef.current = saveId;
      pendingPreserveRef.current = null;
    }
    setLeagueId(urlLeagueId);
  }, [urlLeagueId, saveId]);
  useEffect(() => {
    setView(urlView ?? 'season');
  }, [urlView, saveId]);

  const seasonsState = useAsync<HistorySeasons>(saveId, (signal) =>
    fetchHistorySeasons(saveId as string, signal),
  );
  // MSS-084: ignore stale seasons from the previous save while reloading.
  const seasonsRaw = seasonsState.data?.seasons ?? [];
  const seasons =
    seasonsState.data !== null &&
    seasonsState.data !== undefined &&
    seasonsState.data.saveId === saveId
      ? seasonsRaw
      : [];
  const latestSeason = seasons.length > 0 ? seasons[seasons.length - 1]!.seasonNumber : null;

  const explicitSeasonInvalid =
    urlSeason !== null && seasons.length > 0 && !seasons.some((row) => row.seasonNumber === urlSeason);

  useEffect(() => {
    if (seasons.length === 0) {
      return;
    }
    setSeasonNumber((current) => {
      if (urlSeason !== null && seasons.some((row) => row.seasonNumber === urlSeason)) {
        return urlSeason;
      }
      if (urlSeason !== null) {
        return current;
      }
      if (current !== null && seasons.some((row) => row.seasonNumber === current)) {
        return current;
      }
      return seasons[seasons.length - 1]!.seasonNumber;
    });
  }, [seasons, urlSeason]);

  const competitionsKey =
    saveId && seasonNumber !== null && !explicitSeasonInvalid
      ? `${saveId}/standings-s${seasonNumber}`
      : null;
  const competitionsState = useAsync<HistoryCompetitions>(competitionsKey, (signal) =>
    fetchHistoryCompetitions(saveId as string, seasonNumber as number, signal),
  );
  // MSS-084: ignore stale competitions from the previous season/save while
  // the new season loads, so the league dropdown never shows the wrong
  // season's or the wrong save's options.
  const competitionsRaw = competitionsState.data?.competitions ?? [];
  const competitionsFresh =
    competitionsState.data !== null &&
    competitionsState.data !== undefined &&
    seasonNumber !== null &&
    competitionsState.data.saveId === saveId &&
    competitionsState.data.seasonNumber === seasonNumber
      ? competitionsRaw
      : [];
  const competitions = competitionsFresh;
  const explicitLeagueInvalid =
    urlLeagueId !== null &&
    competitions.length > 0 &&
    !competitions.some((row) => row.leagueId === urlLeagueId);
  // MSS-084 correction: an in-app season switch that preserved a league id
  // which is absent in the destination season must fall back (with a URL
  // rewrite), not show the explicit invalid-league error. Directly entered
  // invalid URLs have no pending preservation and keep the error.
  const pendingPreserve = pendingPreserveRef.current;
  const isPendingPreserveMissing =
    pendingPreserve !== null &&
    pendingPreserve.league !== null &&
    urlSeason !== null &&
    urlLeagueId !== null &&
    pendingPreserve.season === urlSeason &&
    pendingPreserve.league === urlLeagueId &&
    competitions.length > 0 &&
    !competitions.some((row) => row.leagueId === urlLeagueId);
  const showExplicitLeagueInvalid = explicitLeagueInvalid && !isPendingPreserveMissing;

  useEffect(() => {
    if (competitions.length === 0) {
      return;
    }
    setLeagueId((current) => {
      if (urlLeagueId !== null && competitions.some((row) => row.leagueId === urlLeagueId)) {
        return urlLeagueId;
      }
      if (urlLeagueId !== null) {
        return current;
      }
      // MSS-084: a league-less URL is authoritative and defaults to
      // Superleague/first. The only exception is an in-app season switch
      // whose router URL has not applied yet (component-only updates keep
      // url props stale): pendingPreserveRef carries the preserved league for
      // the current season until the router echoes it back.
      const pending = pendingPreserveRef.current;
      if (
        pending?.league != null &&
        seasonNumber !== null &&
        pending.season === seasonNumber &&
        competitions.some((row) => row.leagueId === pending.league)
      ) {
        return pending.league;
      }
      // MSS-084 default: Superleague first, smallest id only for legacy saves.
      return defaultLeagueIdFor(competitions) ?? current ?? urlLeagueId;
    });
  }, [competitions, urlLeagueId, seasonNumber]);

  // MSS-084 correction: rewrite the URL when a preserved league is missing
  // in the destination season so selector, standings and shareable URL agree.
  // Runs only for the tracked in-app switch; direct invalid URLs keep error.
  // The router applies the optimistic URL asynchronously, so keep the pending
  // marker while the URL is still stale instead of clearing it early.
  // The provisional preserved-league URL was already pushed by the season
  // switch, so the fallback correction uses replace to avoid leaving the
  // invalid intermediate URL in browser history (Back must return to the
  // previous season, not to the invalid URL).
  useEffect(() => {
    const pending = pendingPreserveRef.current;
    if (pending === null || pending.league === null) {
      if (pending !== null && pending.league === null) {
        pendingPreserveRef.current = null;
      }
      return;
    }
    if (urlSeason !== pending.season || urlLeagueId !== pending.league) {
      return;
    }
    if (competitions.length === 0) {
      return;
    }
    if (competitions.some((row) => row.leagueId === pending.league)) {
      pendingPreserveRef.current = null;
      return;
    }
    const fallback = defaultLeagueIdFor(competitions);
    pendingPreserveRef.current = null;
    if (fallback !== null) {
      setLeagueId(fallback);
      pushSelection({ league: fallback, season: urlSeason, view }, { replace: true });
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [competitions, urlSeason, urlLeagueId]);

  const leagueKnown = leagueId !== null && competitions.some((row) => row.leagueId === leagueId);
  const seasonEntry = seasons.find((row) => row.seasonNumber === seasonNumber) ?? null;
  const isLatestSeason = seasonNumber !== null && seasonNumber === latestSeason;
  const useCurrentTotals =
    seasonEntry !== null && !seasonEntry.isComplete && isLatestSeason && leagueKnown;

  const stagesKey =
    saveId && seasonNumber !== null && leagueId !== null && leagueKnown && !explicitSeasonInvalid
      ? `${saveId}/standings-stages-s${seasonNumber}-l${leagueId}`
      : null;
  const stagesState = useAsync<HistoryStages>(stagesKey, (signal) =>
    fetchHistoryStages(saveId as string, seasonNumber as number, leagueId as number, signal),
  );

  const placementsKey =
    saveId && seasonNumber !== null && leagueId !== null && leagueKnown && !explicitSeasonInvalid
      ? `${saveId}/standings-placements-s${seasonNumber}-l${leagueId}`
      : null;
  const placementsState = useAsync<SeasonPlacements>(placementsKey, (signal) =>
    fetchSeasonPlacements(saveId as string, seasonNumber as number, leagueId as number, signal),
  );

  const historyTableKey =
    saveId &&
    seasonNumber !== null &&
    leagueId !== null &&
    leagueKnown &&
    !explicitSeasonInvalid &&
    seasonEntry !== null &&
    (seasonEntry.isComplete || !isLatestSeason)
      ? `${saveId}/standings-table-s${seasonNumber}-l${leagueId}`
      : null;
  const historyTableState = useAsync<HistorySeasonTable>(historyTableKey, (signal) =>
    fetchHistorySeasonTable(saveId as string, seasonNumber as number, leagueId as number, signal),
  );

  const currentTotalsKey =
    saveId && useCurrentTotals && leagueId !== null
      ? `${saveId}/standings-current-l${leagueId}`
      : null;
  const currentTotalsState = useAsync<CurrentStandings>(currentTotalsKey, (signal) =>
    fetchCurrentStandings(saveId as string, leagueId as number, signal),
  );

  function pushSelection(next: StandingsSelection, options?: { replace?: boolean }): void {
    onStandingsChange(next, options);
  }

  // MSS-084: only use payloads that match the current save/season/league
  // request. Stale payloads from the previous key stay in useAsync state
  // while the new request loads; ignoring them prevents season/league/save
  // mismatched headings. Every guarded contract carries saveId, so a stale
  // payload from another save with matching numeric ids is also dropped.
  const placementsRaw = placementsState.data;
  const placements =
    placementsRaw !== null &&
    placementsRaw !== undefined &&
    seasonNumber !== null &&
    leagueId !== null &&
    placementsRaw.saveId === saveId &&
    placementsRaw.seasonNumber === seasonNumber &&
    placementsRaw.leagueId === leagueId
      ? placementsRaw
      : null;
  const stagesRaw = stagesState.data;
  const stages =
    stagesRaw !== null &&
    stagesRaw !== undefined &&
    seasonNumber !== null &&
    leagueId !== null &&
    stagesRaw.saveId === saveId &&
    stagesRaw.seasonNumber === seasonNumber &&
    stagesRaw.leagueId === leagueId
      ? (stagesRaw.stages ?? [])
      : [];
  const activeCompetition = competitions.find((row) => row.leagueId === leagueId) ?? null;
  const leagueKind =
    placements?.leagueKind ??
    activeCompetition?.kind ??
    'Feeder';
  const leagueLevel =
    placements?.leagueLevel ??
    activeCompetition?.leagueLevel ??
    null;
  const leagueDivision =
    placements?.feederDivision ??
    activeCompetition?.feederDivision ??
    null;
  const tierLabel = leagueLevelLabel(leagueLevel, leagueDivision, leagueKind);

  const totalsRows: TotalsRow[] = useMemo(() => {
    if (useCurrentTotals) {
      const current = currentTotalsState.data;
      // MSS-084: drop stale current totals from the previous league/save
      // while reloading. The contract is save-scoped, so a matching league
      // id from another save must not render under this save's URL.
      if (
        !current ||
        current.standings.length === 0 ||
        current.saveId !== saveId ||
        (leagueId !== null && current.leagueId !== leagueId)
      ) {
        return [];
      }
      return current.standings.map((row) => ({
        athleteId: row.athleteId,
        name: row.name,
        seasonRank: row.seasonRank,
        totalChampionshipPointsThousandths: row.totalChampionshipPointsThousandths,
        totalStageScoreThousandths: row.totalStageScoreThousandths,
        roundWins: row.roundWins,
        stageWins: row.stageWins,
        isChampion: row.isChampion,
      }));
    }
    const table = historyTableState.data;
    // MSS-084: drop stale final tables from the previous season/league/save.
    if (
      !table ||
      table.standings.length === 0 ||
      table.saveId !== saveId ||
      (seasonNumber !== null && table.seasonNumber !== seasonNumber) ||
      (leagueId !== null && table.leagueId !== leagueId)
    ) {
      return [];
    }
    return table.standings.map((row) => ({
      athleteId: row.athleteId,
      name: row.name,
      seasonRank: row.seasonRank,
      totalChampionshipPointsThousandths: row.totalChampionshipPointsThousandths,
      totalStageScoreThousandths: row.totalStageScoreThousandths,
      roundWins: row.roundWins,
      stageWins: row.stageWins,
      isChampion: row.isChampion,
    }));
  }, [useCurrentTotals, currentTotalsState.data, historyTableState.data, seasonNumber, leagueId, saveId]);

  const totalsByAthlete = useMemo(() => {
    const map = new Map<number, TotalsRow>();
    for (const row of totalsRows) {
      map.set(row.athleteId, row);
    }
    return map;
  }, [totalsRows]);

  const combinedRows: CombinedMatrixRow[] = useMemo(() => {
    if (!placements || placements.athletes.length === 0) {
      return [];
    }
    return placements.athletes.map((athlete) => {
      const totals = totalsByAthlete.get(athlete.athleteId);
      return {
        athleteId: athlete.athleteId,
        name: totals?.name ?? athlete.name,
        seasonRank: totals?.seasonRank ?? null,
        totalChampionshipPointsThousandths: totals?.totalChampionshipPointsThousandths ?? null,
        stageWins: totals?.stageWins ?? (placements.completedStages === 0 ? 0 : null),
        roundWins: totals?.roundWins ?? null,
        sportingColorName: athlete.sportingColorName,
        imageUrl: athlete.imageUrl,
        currentEffectiveBonusThousandths: athlete.currentEffectiveBonusThousandths,
      };
    });
  }, [placements, totalsByAthlete]);

  const sortedRows = useMemo(() => sortMatrixRows(combinedRows, sortKey), [combinedRows, sortKey]);

  const cellMap = useMemo(
    () => buildCellMap(placements?.placements ?? []),
    [placements],
  );

  const stageColumns = useMemo(() => {
    if (stageWindow === 'completed') {
      const completed = placements?.placements ?? [];
      const { length } = visibleStageColumns('completed', completed);
      if (length === 0) {
        return allStageColumns();
      }
    }
    return visibleStageColumns(stageWindow, placements?.placements ?? []);
  }, [stageWindow, placements]);

  const completedStages = placements?.completedStages ?? 0;
  const lastCompletedStage = maxCompletedStage(placements?.placements ?? []);
  const inProgressStage = stages.find((row) => !row.isComplete && row.completedRounds > 0) ?? null;
  const totalsLoading = useCurrentTotals ? currentTotalsState.loading : historyTableState.loading;
  const totalsError = useCurrentTotals ? currentTotalsState.error : historyTableState.error;

  // MSS-068: all hooks must run unconditionally on every render, including
  // loading/error/empty/invalid states. These derivations use safe defaults
  // (empty competitions) while async queries resolve, so moving them above
  // the early returns keeps hook order stable without extra fetches.
  const seasonOptions = [...seasons].sort((a, b) => a.seasonNumber - b.seasonNumber);
  const tierGroups = useMemo(
    () =>
      groupLeaguesByTier(
        competitions.map((row) => ({
          leagueId: row.leagueId,
          name: row.name,
          kind: row.kind,
          feederDivision: row.feederDivision ?? null,
          leagueLevel: row.leagueLevel ?? null,
        })),
      ),
    [competitions],
  );
  const tierSections = useMemo(
    () =>
      (
        [
          ['Superleague', tierGroups.superleague],
          ['Feeder 1', tierGroups.feeder1],
          ['Feeder 2', tierGroups.feeder2],
          ['Feeder 3', tierGroups.feeder3],
          ['Feeder', tierGroups.legacyFeeder],
        ] as const
      ).filter(([, rows]) => rows.length > 0),
    [tierGroups],
  );

  if (seasonsState.loading && seasons.length === 0) {
    return <Loading label="Loading standings…" />;
  }

  if (seasonsState.error) {
    return (
      <Notice tone="error" title="Standings unavailable">
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
      <Notice tone="empty" title="No seasons yet">
        <p>This save has no persisted seasons to rank.</p>
      </Notice>
    );
  }

  if (explicitSeasonInvalid) {
    return (
      <Notice tone="error" title="Season not found">
        <p>
          Season {urlSeason} does not exist in this save. Pick an available season below; no
          other season was substituted.
        </p>
        <p className="live-buttons">
          <Link to={savesPath()} className="ghost-button">
            Back to saves
          </Link>
        </p>
      </Notice>
    );
  }

  if (showExplicitLeagueInvalid) {
    return (
      <Notice tone="error" title="League not part of this season">
        <p>
          League {urlLeagueId} is not part of Season {seasonNumber}. No other league was
          substituted; pick a league from this season below.
        </p>
      </Notice>
    );
  }

  return (
    <div className="dashboard standings-page">
      <div className="toolbar" role="group" aria-label="Standings filters">
        <label className="field">
          <span>League</span>
          <select
            value={leagueId ?? ''}
            disabled={competitions.length === 0}
            onChange={(event) => {
              const next = Number.parseInt(event.target.value, 10);
              const league = Number.isNaN(next) ? null : next;
              setLeagueId(league);
              pushSelection({ league, season: seasonNumber, view });
            }}
          >
            {tierSections.map(([label, rows]) => (
              <optgroup key={label} label={label}>
                {rows.map((row) => (
                  <option key={row.leagueId} value={row.leagueId}>
                    {row.name}
                  </option>
                ))}
              </optgroup>
            ))}
          </select>
        </label>
        <label className="field">
          <span>Season</span>
          <select
            value={seasonNumber ?? ''}
            onChange={(event) => {
              const next = Number.parseInt(event.target.value, 10);
              const season = Number.isNaN(next) ? null : next;
              // MSS-084: keep the selected league by stable id in the
              // shareable URL so standings and URL agree after the switch.
              // When the preserved id is missing in the destination season,
              // the correction effect falls back to Superleague/first and
              // rewrites the URL instead of showing the invalid-league error.
              const preserved = leagueId;
              setSeasonNumber(season);
              if (season !== null) {
                pendingPreserveRef.current = { season, league: preserved };
              } else {
                pendingPreserveRef.current = null;
              }
              pushSelection({ league: preserved, season, view });
            }}
          >
            {seasonOptions.map((row) => (
              <option key={row.seasonNumber} value={row.seasonNumber}>
                Season {row.seasonNumber}
                {row.isComplete ? '' : ' (in progress)'}
              </option>
            ))}
          </select>
        </label>
        <div className="field">
          <span>View</span>
          <div className="segmented" role="group" aria-label="Table views">
            <button
              type="button"
              className={view === 'season' ? 'nav-item current' : 'nav-item'}
              aria-pressed={view === 'season'}
              onClick={() => {
                setView('season');
                pushSelection({ league: leagueId, season: seasonNumber, view: 'season' });
              }}
            >
              Season table
            </button>
            <button
              type="button"
              className={view === 'matrix' ? 'nav-item current' : 'nav-item'}
              aria-pressed={view === 'matrix'}
              onClick={() => {
                setView('matrix');
                pushSelection({ league: leagueId, season: seasonNumber, view: 'matrix' });
              }}
            >
              Stage placements
            </button>
          </div>
        </div>
        <label className="field">
          <span>Sort rows by</span>
          <select
            value={sortKey}
            onChange={(event) => {
              const next = event.target.value;
              setSortKey(next === 'wins' || next === 'points' ? next : 'rank');
            }}
          >
            <option value="rank">Season rank</option>
            <option value="wins">Stage wins</option>
            <option value="points">Championship points</option>
          </select>
        </label>
        <div className="toolbar-end">
          {seasonsState.loading || competitionsState.loading ? (
            <span className="muted small">Refreshing…</span>
          ) : null}
          {leagueId !== null && seasonNumber !== null ? (
            <>
              <Link
                to={historyPath(saveId, { season: seasonNumber, competition: leagueId })}
                className="ghost-button"
              >
                History
              </Link>
              <Link to={livePath(saveId, { league: leagueId })} className="ghost-button">
                Live
              </Link>
            </>
          ) : null}
          <InfoDisclosure>
            <p>
              League, season and view are reflected in the URL (`?league=&amp;season=&amp;view=`
              or `/leagues/:leagueId/standings`) so the table can be copied or opened in another
              tab. Each tab keeps its own save context.
            </p>
          </InfoDisclosure>
        </div>
      </div>
      {competitionsState.error ? (
        <p className="muted small">Leagues: {competitionsState.error}</p>
      ) : null}

      <Card
        eyebrow="Progress"
        title={
          placements
            ? `${placements.leagueName} · ${tierLabel} — Season ${placements.seasonNumber} · ${completedStages}/32 stages`
            : 'Season progress'
        }
      >
        {placementsState.loading && !placements ? (
          <Loading label="Loading league table…" />
        ) : placementsState.error ? (
          <Notice tone="error" title="League table unavailable">
            <p>{placementsState.error}</p>
          </Notice>
        ) : !placements ? (
          <Notice tone="empty" title="No league selected">
            <p>Pick a league and season to inspect its table.</p>
          </Notice>
        ) : (
          <>
            <dl className="stats">
              <div>
                <dt>Completed stages</dt>
                <dd>
                  {completedStages} / 32
                  {lastCompletedStage !== null ? ` · updated after Stage ${lastCompletedStage}` : ''}
                </dd>
              </div>
              <div>
                <dt>Season state</dt>
                <dd>{placements.isSeasonComplete ? 'Complete · final table' : 'In progress · live table'}</dd>
              </div>
              <div>
                <dt>In-progress stage</dt>
                <dd>
                  {inProgressStage
                    ? `Stage ${inProgressStage.stageNumber} (${inProgressStage.completedRounds}/16 rounds) · not shown`
                    : 'None · only completed stages appear'}
                </dd>
              </div>
            </dl>
            <p className="muted small">
              Tables accumulate persisted stage data only. An incomplete stage never
              contributes placements, points or bonus to either view.
            </p>
            <p className="muted small">
              {tierLabel} bonus {tierBonusLabel(leagueLevel)} applies to future rounds; earned
              bonus activates from the next stage. {TIER_BONUS_SCALE_LINE}
            </p>
          </>
        )}
      </Card>

      {view === 'season' ? (
        <Card
          eyebrow="Season table"
          title={
            placements
              ? `${placements.leagueName} — Season ${placements.seasonNumber}${placements.isSeasonComplete ? ' · final' : ''}`
              : 'Season table'
          }
        >
          {totalsLoading && totalsRows.length === 0 ? (
            <Loading label="Loading season table…" />
          ) : totalsError ? (
            <Notice tone="error" title="Season table unavailable">
              <p>{totalsError}</p>
            </Notice>
          ) : totalsRows.length === 0 ? (
            <Notice tone="empty" title="No standings yet">
              <p>
                Complete a stage on the Live tab to populate championship standings. Opening
                this page never requires finishing the running stage.
              </p>
            </Notice>
          ) : (
            <>
              <div className="table-wrap">
                <table className="data-table">
                  <thead>
                    <tr>
                      <th scope="col" className="numeric">Rank</th>
                      <th scope="col">Card</th>
                      <th scope="col" className="numeric">Champ pts</th>
                      <th scope="col" className="numeric">Stage W</th>
                      <th scope="col" className="numeric">Round W</th>
                      <th scope="col" className="numeric">Stage score</th>
                    </tr>
                  </thead>
                  <tbody>
                    {sortedRows.map((row) => {
                      const totals = totalsByAthlete.get(row.athleteId);
                      if (!totals) {
                        return null;
                      }
                      return (
                        <tr key={row.athleteId}>
                          <td className="numeric">{totals.seasonRank}</td>
                          <td>
                            <div className="card-cell">
                              {row.imageUrl ? (
                                <img className="card-thumb" src={row.imageUrl} alt="" loading="lazy" />
                              ) : (
                                <span className="card-thumb card-thumb-fallback" aria-hidden="true">
                                  {row.name.slice(0, 2).toUpperCase()}
                                </span>
                              )}
                              <span className="card-identity">
                                <AthleteLink saveId={saveId} athleteId={row.athleteId} name={row.name} />
                                {totals.isChampion ? <span className="card-sub"> · Champion</span> : null}
                              </span>
                            </div>
                          </td>
                          <td className="numeric">{formatPoints(totals.totalChampionshipPointsThousandths)}</td>
                          <td className="numeric">{totals.stageWins}</td>
                          <td className="numeric">{totals.roundWins}</td>
                          <td className="numeric">{formatPoints(totals.totalStageScoreThousandths)}</td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
              <p className="muted small">
                {useCurrentTotals
                  ? 'Live cumulative table from persisted completed stages; final ranks persist at season end.'
                  : 'Persisted final table; completed seasons never change when new stages run.'}
              </p>
            </>
          )}
        </Card>
      ) : (
        <Card
          eyebrow="Stage placements · bonus calibration"
          title={
            placements
              ? `${placements.leagueName} — Season ${placements.seasonNumber} · P1–P32 per completed stage`
              : 'Stage placements'
          }
        >
          {placementsState.loading && !placements ? (
            <Loading label="Loading placements…" />
          ) : placementsState.error ? (
            <Notice tone="error" title="Placements unavailable">
              <p>{placementsState.error}</p>
            </Notice>
          ) : !placements || placements.athletes.length === 0 ? (
            <Notice tone="empty" title="No roster yet">
              <p>This league has no season roster to align.</p>
            </Notice>
          ) : (
            <>
              <div className="live-controls standings-matrix-controls">
                <label className="field">
                  <span>Stage window</span>
                  <select
                    value={stageWindow}
                    onChange={(event) => {
                      const next = event.target.value as StageWindow;
                      setStageWindow(
                        next === 'completed' ||
                        next === '1-8' ||
                        next === '9-16' ||
                        next === '17-24' ||
                        next === '25-32'
                          ? next
                          : 'all',
                      );
                    }}
                  >
                    <option value="all">All 32 stages</option>
                    <option value="completed">Completed only</option>
                    <option value="1-8">Stages 1–8</option>
                    <option value="9-16">Stages 9–16</option>
                    <option value="17-24">Stages 17–24</option>
                    <option value="25-32">Stages 25–32</option>
                  </select>
                </label>
                <label className="check-row">
                  <input
                    type="checkbox"
                    checked={showBonus}
                    onChange={(event) => setShowBonus(event.target.checked)}
                  />
                  Show earned bonus in cells
                </label>
              </div>
              {completedStages === 0 ? (
                <Notice tone="empty" title="No completed stages yet">
                  <p>
                    All 32 stage columns are blank until Stage 1 persists. The 32-athlete
                    roster below stays aligned to this season and league.
                  </p>
                </Notice>
              ) : null}
              <div className="matrix-wrap" role="region" aria-label="Stage placement matrix" tabIndex={0}>
                <table className="data-table matrix-table">
                  <thead>
                    <tr>
                      <th scope="col" className="numeric sticky sticky-rank">
                        Rank
                      </th>
                      <th scope="col" className="sticky sticky-athlete">
                        Card
                      </th>
                      <th scope="col" className="numeric sticky sticky-points">
                        Champ pts
                      </th>
                      <th scope="col" className="numeric sticky sticky-wins">
                        Stage W
                      </th>
                      <th scope="col" className="numeric sticky sticky-bonus" title="Time-dependent current effective bonus, not the bonus used in earlier stages.">
                        Bonus now*
                      </th>
                      {stageColumns.map((stage) => (
                        <th key={stage} scope="col" className="matrix-stage-head" title={`Stage ${stage}${cellMap.size > 0 && [...cellMap.values()].some((byStage) => byStage.has(stage)) ? ' · completed' : ' · not yet completed'}`}>
                          S{stage}
                        </th>
                      ))}
                    </tr>
                  </thead>
                  <tbody>
                    {sortedRows.map((row) => (
                      <tr key={row.athleteId}>
                        <td className="numeric sticky sticky-rank">
                          {row.seasonRank ?? '—'}
                        </td>
                        <td className="sticky sticky-athlete">
                          <div className="card-cell">
                            {row.imageUrl ? (
                              <img className="card-thumb" src={row.imageUrl} alt="" loading="lazy" />
                            ) : (
                              <span className="card-thumb card-thumb-fallback" aria-hidden="true">
                                {row.name.slice(0, 2).toUpperCase()}
                              </span>
                            )}
                            <span className="card-identity">
                              <AthleteLink saveId={saveId} athleteId={row.athleteId} name={row.name} />
                              <span className="card-sub">{row.sportingColorName}</span>
                            </span>
                          </div>
                        </td>
                        <td className="numeric sticky sticky-points">
                          {row.totalChampionshipPointsThousandths !== null
                            ? formatPoints(row.totalChampionshipPointsThousandths)
                            : '—'}
                        </td>
                        <td className="numeric sticky sticky-wins">{row.stageWins ?? '—'}</td>
                        <td
                          className="numeric sticky sticky-bonus"
                          title="Current effective bonus right now; bonus earned in a stage activates only from the next stage."
                        >
                          {formatBonus(row.currentEffectiveBonusThousandths)}
                        </td>
                        {stageColumns.map((stage) => {
                          const cell = cellMap.get(row.athleteId)?.get(stage);
                          const label = cell ? placeLabel(cell.stageRank) : '—';
                          return (
                            <td
                              key={stage}
                              className={`numeric matrix-cell ${podiumClass(cell?.stageRank ?? null)}`}
                              title={cellTitle(row.name, stage, cell)}
                              aria-label={cellTitle(row.name, stage, cell)}
                            >
                              <span className="matrix-place">{label}</span>
                              {showBonus && cell ? (
                                <span className="matrix-bonus">{formatBonus(cell.earnedBonusThousandths)}</span>
                              ) : null}
                            </td>
                          );
                        })}
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              <details className="advanced">
                <summary>Legend · placements, points and bonus activation</summary>
                <ul className="muted small">
                  <li>
                    <strong>P1–P32</strong> is the creature&apos;s actual finishing place in that
                    completed stage, not its running season position. Blank means not yet
                    completed, never zero.
                  </li>
                  <li>
                    <strong>P1 ★ gold</strong>, <strong>P2–P3 silver/bronze</strong> and{' '}
                    <strong>P4–P10</strong> shading are scanning aids only; every cell also
                    carries its text label for non-color access.
                  </li>
                  <li>
                    <strong>Champ pts</strong> are cumulative season championship points from
                    persisted stage results (32-position table, no bonus multiplier).
                  </li>
                  <li>
                    Hover or focus a cell for that stage&apos;s earned bonus and championship
                    points. Earned bonus becomes active only from the <em>next</em> stage;
                    Stage 32 bonus first applies next season at decay weight.
                  </li>
                  <li>
                    <strong>Bonus now*</strong> is the time-dependent current effective bonus
                    and was not retroactively active in earlier stages. Bonus changes future
                    round point value, never shuffle probability.
                  </li>
                </ul>
              </details>
            </>
          )}
        </Card>
      )}
    </div>
  );
}
