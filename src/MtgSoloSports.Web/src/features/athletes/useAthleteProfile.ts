import { useEffect, useState } from 'react';
import { ApiError, apiErrorMessage } from '../../shared/api/http';
import { fetchAthleteProfile, type AthleteProfile } from './athleteApi';
import { fetchAthleteStories, type StoryEventItem } from '../stories/storiesApi';
import { fetchRecords, type CareerRecord } from '../records/recordsApi';

export interface AthleteProfileState {
  profile: AthleteProfile | null;
  stories: StoryEventItem[];
  recordHoldings: CareerRecord[];
  loading: boolean;
  error: string | null;
  notFound: boolean;
  refresh: () => void;
}

/**
 * Reads one athlete profile from transactional projections. Read-only: never
 * triggers simulation and never decompresses round history.
 */
export function useAthleteProfile(
  saveId: string | null,
  athleteId: number | null,
): AthleteProfileState {
  const [profile, setProfile] = useState<AthleteProfile | null>(null);
  const [stories, setStories] = useState<StoryEventItem[]>([]);
  const [recordHoldings, setRecordHoldings] = useState<CareerRecord[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notFound, setNotFound] = useState(false);
  const [revision, setRevision] = useState(0);

  useEffect(() => {
    if (!saveId || athleteId === null) {
      setProfile(null);
      setStories([]);
      setRecordHoldings([]);
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
      const loaded = await fetchAthleteProfile(saveId, athleteId, signal);
      let feed: StoryEventItem[] = [];
      try {
        const storiesFeed = await fetchAthleteStories(saveId, athleteId, 20, signal);
        feed = storiesFeed.stories;
      } catch (failure) {
        if (failure instanceof DOMException && failure.name === 'AbortError') {
          throw failure;
        }
        feed = [];
      }
      let holdings: CareerRecord[] = [];
      try {
        const records = await fetchRecords(saveId, signal);
        holdings = records.records.filter((record) =>
          record.holders.some((holder) => holder.athleteId === athleteId),
        );
      } catch (failure) {
        if (failure instanceof DOMException && failure.name === 'AbortError') {
          throw failure;
        }
        holdings = [];
      }
      return { loaded, feed, holdings };
    })()
      .then(({ loaded, feed, holdings }) => {
        setProfile(loaded);
        setStories(feed);
        setRecordHoldings(holdings);
        setLoading(false);
      })
      .catch((failure: unknown) => {
        if (failure instanceof DOMException && failure.name === 'AbortError') {
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
    refresh: () => {
      setRevision((value) => value + 1);
    },
  };
}
