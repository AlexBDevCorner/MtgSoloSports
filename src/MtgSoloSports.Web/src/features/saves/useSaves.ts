import { useCallback, useEffect, useState } from 'react';
import { apiErrorMessage } from '../../shared/api/http';
import { deleteSave, listSaves, type SaveSummary } from './savesApi';

export interface SavesState {
  saves: SaveSummary[];
  loading: boolean;
  error: string | null;
  deletingId: string | null;
  refresh: () => void;
  remove: (saveId: string) => Promise<void>;
  addCreated: (save: SaveSummary) => void;
}

export function useSaves(): SavesState {
  const [saves, setSaves] = useState<SaveSummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [deletingId, setDeletingId] = useState<string | null>(null);
  const [revision, setRevision] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError(null);
    listSaves(controller.signal)
      .then((rows) => {
        setSaves(rows);
        setLoading(false);
      })
      .catch((failure: unknown) => {
        if (failure instanceof DOMException && failure.name === 'AbortError') {
          return;
        }
        setError(apiErrorMessage(failure));
        setLoading(false);
      });
    return () => {
      controller.abort();
    };
  }, [revision]);

  const refresh = useCallback(() => {
    setRevision((value) => value + 1);
  }, []);

  const addCreated = useCallback((save: SaveSummary) => {
    setSaves((current) => [save, ...current.filter((row) => row.saveId !== save.saveId)]);
  }, []);

  const remove = useCallback(async (saveId: string) => {
    setDeletingId(saveId);
    try {
      await deleteSave(saveId);
      setSaves((current) => current.filter((row) => row.saveId !== saveId));
    } finally {
      setDeletingId(null);
    }
  }, []);

  return { saves, loading, error, deletingId, refresh, remove, addCreated };
}
