import { useEffect, useState } from 'react';
import { ApiError, apiErrorMessage } from '../../shared/api/http';
import {
  fetchSaveDetail,
  fetchSeason1Leagues,
  fetchSeasonProgress,
  fetchSeasonStatus,
  type Season1Leagues,
  type SeasonProgress,
  type SeasonStatus,
} from './dashboardApi';
import type { SaveDetail } from '../saves/savesApi';
import type { StoryEventItem } from '../stories/storiesApi';
import { fetchRecentStories } from '../stories/storiesApi';
import { fetchCurrentStandings, type CurrentStandingRow } from '../athletes/athleteApi';
import { fetchHonours, type Honour } from '../records/recordsApi';
import { fetchHallOfFame, fetchRecords, type HallOfFameLeader, type Records } from '../records/recordsApi';

export interface DashboardLeaderBoard {
  leagueId: number;
  leagueName: string;
  leagueKind: string;
  total: number;
  top: CurrentStandingRow[];
}

export interface DashboardData {
  detail: SaveDetail;
  progress: SeasonProgress;
  rosters: Season1Leagues | null;
  status: SeasonStatus | null;
  stories: StoryEventItem[];
  leaders: DashboardLeaderBoard[];
  superleagueComposition: Array<{ color: string; count: number }>;
  recentHonours: Honour[];
  records: Records | null;
  hallOfFame: HallOfFameLeader[];
}

export interface DashboardState {
  data: DashboardData | null;
  loading: boolean;
  error: string | null;
  notFound: boolean;
  refresh: () => void;
}

export function useDashboard(saveId: string | null): DashboardState {
  const [data, setData] = useState<DashboardData | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notFound, setNotFound] = useState(false);
  const [revision, setRevision] = useState(0);

  useEffect(() => {
    if (!saveId) {
      setData(null);
      setLoading(false);
      setError(null);
      setNotFound(false);
      return;
    }
    const controller = new AbortController();
    const { signal } = controller;
    setLoading(true);
    setError(null);
    setNotFound(false);

    (async () => {
      const detail = await fetchSaveDetail(saveId, signal);
      const progress = await fetchSeasonProgress(saveId, detail.currentSeason, signal);
      let status: SeasonStatus | null = null;
      try {
        status = await fetchSeasonStatus(saveId, signal);
      } catch (failure) {
        if (failure instanceof DOMException && failure.name === 'AbortError') {
          throw failure;
        }
        status = null;
      }
      let rosters: Season1Leagues | null = null;
      try {
        rosters = await fetchSeason1Leagues(saveId, signal);
      } catch (failure) {
        if (failure instanceof ApiError && failure.status === 404) {
          rosters = null;
        } else if (failure instanceof DOMException && failure.name === 'AbortError') {
          throw failure;
        } else if (failure instanceof ApiError && failure.status === 400) {
          rosters = null;
        } else {
          throw failure;
        }
      }
      const stories = await loadStories(saveId, signal);
      const { leaders, superleagueComposition } = await loadLeaders(saveId, progress, signal);
      const recentHonours = await loadHonours(saveId, signal);
      const records = await loadRecords(saveId, signal);
      const hallOfFame = await loadHallOfFame(saveId, signal);
      return { detail, progress, rosters, status, stories, leaders, superleagueComposition, recentHonours, records, hallOfFame };
    })()
      .then((loaded) => {
        setData(loaded);
        setLoading(false);
      })
      .catch((failure: unknown) => {
        if (failure instanceof DOMException && failure.name === 'AbortError') {
          return;
        }
        if (failure instanceof ApiError && failure.status === 404) {
          setNotFound(true);
          setError('That save no longer exists.');
        } else {
          setError(apiErrorMessage(failure));
        }
        setLoading(false);
      });

    return () => {
      controller.abort();
    };
  }, [saveId, revision]);

  return {
    data,
    loading,
    error,
    notFound,
    refresh: () => {
      setRevision((value) => value + 1);
    },
  };
}

async function loadStories(saveId: string, signal: AbortSignal): Promise<StoryEventItem[]> {
  try {
    const feed = await fetchRecentStories(saveId, 20, signal);
    return feed.stories;
  } catch (failure) {
    if (failure instanceof DOMException && failure.name === 'AbortError') {
      throw failure;
    }
    return [];
  }
}

async function loadLeaders(
  saveId: string,
  progress: SeasonProgress,
  signal: AbortSignal,
): Promise<{ leaders: DashboardLeaderBoard[]; superleagueComposition: Array<{ color: string; count: number }> }> {
  const results = await Promise.all(
    progress.leagues.map(async (league) => {
      try {
        const standings = await fetchCurrentStandings(saveId, league.leagueId, signal);
        return { league, standings: standings.standings };
      } catch (failure) {
        if (failure instanceof DOMException && failure.name === 'AbortError') {
          throw failure;
        }
        return { league, standings: [] as CurrentStandingRow[] };
      }
    }),
  );
  const leaders: DashboardLeaderBoard[] = results.map(({ league, standings }) => ({
    leagueId: league.leagueId,
    leagueName: league.leagueName,
    leagueKind: league.leagueKind ?? 'Feeder',
    total: standings.length,
    top: [...standings]
      .sort((a, b) => a.seasonRank - b.seasonRank)
      .slice(0, 5),
  }));
  const superleague = results.find(({ league }) => (league.leagueKind ?? '') === 'Superleague');
  let superleagueComposition: Array<{ color: string; count: number }> = [];
  if (superleague && superleague.standings.length > 0) {
    const counts = new Map<string, number>();
    for (const row of superleague.standings) {
      const key = (row.sportingColorName ?? 'Unknown').trim() || 'Unknown';
      counts.set(key, (counts.get(key) ?? 0) + 1);
    }
    superleagueComposition = [...counts.entries()]
      .map(([color, count]) => ({ color, count }))
      .sort((a, b) => b.count - a.count || a.color.localeCompare(b.color));
  }
  return { leaders, superleagueComposition };
}

async function loadHonours(saveId: string, signal: AbortSignal): Promise<Honour[]> {
  try {
    const honours = await fetchHonours(saveId, signal);
    return [...honours.honours]
      .sort((a, b) => b.seasonNumber - a.seasonNumber || a.leagueName.localeCompare(b.leagueName))
      .slice(0, 10);
  } catch (failure) {
    if (failure instanceof DOMException && failure.name === 'AbortError') {
      throw failure;
    }
    return [];
  }
}

async function loadRecords(saveId: string, signal: AbortSignal): Promise<Records | null> {
  try {
    return await fetchRecords(saveId, signal);
  } catch (failure) {
    if (failure instanceof DOMException && failure.name === 'AbortError') {
      throw failure;
    }
    return null;
  }
}

async function loadHallOfFame(saveId: string, signal: AbortSignal): Promise<HallOfFameLeader[]> {
  try {
    const fame = await fetchHallOfFame(saveId, 5, signal);
    return fame.leaders;
  } catch (failure) {
    if (failure instanceof DOMException && failure.name === 'AbortError') {
      throw failure;
    }
    return [];
  }
}
