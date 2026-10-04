import { useEffect, useState } from 'react';
import { ApiError, apiErrorMessage } from '../../shared/api/http';
import {
  fetchAthleteProfile,
  fetchAthleteRecordHoldings,
  type AthleteProfile,
  type AthleteRecordHolding,
} from './athleteApi';
import { fetchAthleteStories, type StoryEventItem } from '../stories/storiesApi';

export interface AthleteProfileState {
  profile: AthleteProfile | null;
  stories: StoryEventItem[];
  recordHoldings: AthleteRecordHolding[];
  /** Core profile request only; secondary sections never gate this. */
  loading: boolean;
  error: string | null;
  notFound: boolean;
  storiesLoading: boolean;
  storiesError: string | null;
  holdingsLoading: boolean;
  holdingsError: string | null;
  refresh: () => void;
}

function isAbort(failure: unknown): boolean {
  return failure instanceof DOMException && failure.name === 'AbortError';
}

/**
 * Reads one athlete profile from transactional projections plus two
 * independent secondary sections. Read-only: never triggers simulation and
 * never decompresses round history.
 *
 * The core profile request is the only critical path: the page becomes usable
 * as soon as it succeeds. Stories and athlete-specific record holdings start
 * concurrently once save/athlete identity is known, resolve independently, and
 * degrade gracefully without discarding a loaded profile. The global
 * `/records` endpoint is never requested here.
 */
export function useAthleteProfile(
  saveId: string | null,
  athleteId: number | null,
): AthleteProfileState {
  const [profile, setProfile] = useState<AthleteProfile | null>(null);
  const [stories, setStories] = useState<StoryEventItem[]>([]);
  const [recordHoldings, setRecordHoldings] = useState<AthleteRecordHolding[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notFound, setNotFound] = useState(false);
  const [storiesLoading, setStoriesLoading] = useState(false);
  const [storiesError, setStoriesError] = useState<string | null>(null);
  const [holdingsLoading, setHoldingsLoading] = useState(false);
  const [holdingsError, setHoldingsError] = useState<string | null>(null);
  const [revision, setRevision] = useState(0);

  // Core profile: blocking only for the initial athlete shell/content.
  useEffect(() => {
    if (!saveId || athleteId === null) {
      setProfile(null);
      setLoading(false);
      setError(null);
      setNotFound(false);
      return;
    }
    const controller = new AbortController();
    const { signal } = controller;
    let cancelled = false;
    setLoading(true);
    setError(null);
    setNotFound(false);

    fetchAthleteProfile(saveId, athleteId, signal)
      .then((loaded) => {
        if (cancelled || signal.aborted) {
          return;
        }
        setProfile(loaded);
        setLoading(false);
      })
      .catch((failure: unknown) => {
        if (cancelled || signal.aborted || isAbort(failure)) {
          return;
        }
        if (failure instanceof ApiError && failure.status === 404) {
          setNotFound(true);
          setError('That athlete no longer exists.');
        } else {
          setError(apiErrorMessage(failure));
        }
        setLoading(false);
      });

    return () => {
      cancelled = true;
      controller.abort();
    };
  }, [saveId, athleteId, revision]);

  // Secondary stories feed: independent query, own loading/error state.
  useEffect(() => {
    if (!saveId || athleteId === null) {
      setStories([]);
      setStoriesLoading(false);
      setStoriesError(null);
      return;
    }
    const controller = new AbortController();
    const { signal } = controller;
    let cancelled = false;
    setStoriesLoading(true);
    setStoriesError(null);

    fetchAthleteStories(saveId, athleteId, 20, signal)
      .then((feed) => {
        if (cancelled || signal.aborted) {
          return;
        }
        setStories(feed.stories);
        setStoriesLoading(false);
      })
      .catch((failure: unknown) => {
        if (cancelled || signal.aborted || isAbort(failure)) {
          return;
        }
        if (failure instanceof ApiError && failure.status === 404) {
          // Core profile owns 404 presentation; a missing athlete here just
          // means no stories to show.
          setStories([]);
          setStoriesLoading(false);
          return;
        }
        setStoriesError(apiErrorMessage(failure));
        setStoriesLoading(false);
      });

    return () => {
      cancelled = true;
      controller.abort();
    };
  }, [saveId, athleteId, revision]);

  // Secondary athlete-specific record holdings: independent query against the
  // narrow holdings endpoint, never the global /records calculation.
  useEffect(() => {
    if (!saveId || athleteId === null) {
      setRecordHoldings([]);
      setHoldingsLoading(false);
      setHoldingsError(null);
      return;
    }
    const controller = new AbortController();
    const { signal } = controller;
    let cancelled = false;
    setHoldingsLoading(true);
    setHoldingsError(null);

    fetchAthleteRecordHoldings(saveId, athleteId, signal)
      .then((response) => {
        if (cancelled || signal.aborted) {
          return;
        }
        setRecordHoldings(response.holdings);
        setHoldingsLoading(false);
      })
      .catch((failure: unknown) => {
        if (cancelled || signal.aborted || isAbort(failure)) {
          return;
        }
        if (failure instanceof ApiError && failure.status === 404) {
          setRecordHoldings([]);
          setHoldingsLoading(false);
          return;
        }
        setHoldingsError(apiErrorMessage(failure));
        setHoldingsLoading(false);
      });

    return () => {
      cancelled = true;
      controller.abort();
    };
  }, [saveId, athleteId, revision]);

  return {
    profile,
    stories,
    recordHoldings,
    loading,
    error,
    notFound,
    storiesLoading,
    storiesError,
    holdingsLoading,
    holdingsError,
    refresh: () => {
      setRevision((value) => value + 1);
    },
  };
}
