import { useEffect, useState } from 'react';
import { ApiError, apiErrorMessage } from '../../shared/api/http';
import { fetchAthleteProfile, type AthleteProfile } from './athleteApi';

export interface AthleteProfileState {
  profile: AthleteProfile | null;
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
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notFound, setNotFound] = useState(false);
  const [revision, setRevision] = useState(0);

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
    setLoading(true);
    setError(null);
    setNotFound(false);

    fetchAthleteProfile(saveId, athleteId, signal)
      .then((loaded) => {
        setProfile(loaded);
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
    loading,
    error,
    notFound,
    refresh: () => {
      setRevision((value) => value + 1);
    },
  };
}
