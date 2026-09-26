import { useEffect, useState } from 'react';
import { apiErrorMessage, fetchJson } from '../../shared/api/http';

export interface CatalogStats {
  totalAthletes: number;
  countsBySportingColor: Record<string, number>;
  isSufficientForSave: boolean;
}

export interface CatalogState {
  stats: CatalogStats | null;
  loading: boolean;
  error: string | null;
  refresh: () => void;
}

const EMPTY: CatalogStats = {
  totalAthletes: 0,
  countsBySportingColor: {},
  isSufficientForSave: false,
};

export function useCatalogStats(): CatalogState {
  const [stats, setStats] = useState<CatalogStats | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [revision, setRevision] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError(null);
    fetchJson<CatalogStats>('/api/catalog/stats', { signal: controller.signal })
      .then((payload) => {
        setStats(payload);
        setLoading(false);
      })
      .catch((failure: unknown) => {
        if (failure instanceof DOMException && failure.name === 'AbortError') {
          return;
        }
        setError(apiErrorMessage(failure));
        setStats(EMPTY);
        setLoading(false);
      });
    return () => {
      controller.abort();
    };
  }, [revision]);

  return {
    stats,
    loading,
    error,
    refresh: () => {
      setRevision((value) => value + 1);
    },
  };
}
