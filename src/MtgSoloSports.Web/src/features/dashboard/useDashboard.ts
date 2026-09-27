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

export interface DashboardData {
  detail: SaveDetail;
  progress: SeasonProgress;
  rosters: Season1Leagues | null;
  status: SeasonStatus | null;
  stories: StoryEventItem[];
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
      return { detail, progress, rosters, status, stories: await loadStories(saveId, signal) };
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

export function describeNextAction(progress: SeasonProgress, status?: SeasonStatus | null): {
  headline: string;
  detail: string;
} {
  if (status && status.legalNextActions.length > 0) {
    const action = status.legalNextActions[0];
    return {
      headline: `${status.computedPhase} — ${describeAction(action, status)}`,
      detail: `${status.nextActionDetail} Next backend step is POST /api/saves/{saveId}/advance-next-event.`,
    };
  }
  if (progress.isSeasonComplete) {
    return {
      headline: `Season ${progress.seasonNumber} complete`,
      detail:
        'All 32 stages are complete for every active league. Final tables are persisted; postseason movement runs through the season lifecycle.',
    };
  }
  const pending = progress.leagues.filter((league) => !league.isLeagueComplete).length;
  const ready = progress.leagues.filter(
    (league) => !league.isLeagueComplete && league.currentStage === progress.globalStage,
  ).length;
  return {
    headline: `Stage ${progress.globalStage} ready — ${ready} of ${pending} leagues pending`,
    detail: `Synchronous gate: stage ${progress.globalStage} must complete for every active league before stage ${progress.globalStage + 1} can begin. Next backend step is POST /api/saves/{saveId}/stages/complete-all.`,
  };
}

function describeAction(action: string, status: SeasonStatus): string {
  switch (action) {
    case 'SelectColorCup':
      return `SelectColorCup (Season ${status.sourceSeasonNumber} Color Cup field)`;
    case 'RunColorCupIndividual':
      return `RunColorCupIndividual (Season ${status.sourceSeasonNumber} individual)`;
    case 'RunColorCupTeam':
      return `RunColorCupTeam (Season ${status.sourceSeasonNumber} team)`;
    case 'SelectTypeCup':
      return `SelectTypeCup (Season ${status.sourceSeasonNumber} Type Cup field)`;
    case 'RunTypeCupTeam':
      return `RunTypeCupTeam (Season ${status.sourceSeasonNumber} team)`;
    default:
      return action;
  }
}
