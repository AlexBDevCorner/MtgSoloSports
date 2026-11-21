import { useCallback, useEffect, useState } from 'react';
import { apiErrorMessage, isAbort } from '../../shared/api/http';
import { deleteSave, listSaves, type SaveSummary } from './savesApi';

export interface SavesState {
  saves: SaveSummary[];
  loading: boolean;
  error: string | null;
  deletingId: string | null;
  /** MSS-145: last delete failure. The list is refreshed alongside it so the true state stays visible. */
  removeError: string | null;
  refresh: () => void;
  /** Resolves false when the delete failed (the failure is exposed via removeError). */
  remove: (saveId: string) => Promise<boolean>;
  addCreated: (save: SaveSummary) => void;
  clearRemoveError: () => void;
}

export function useSaves(): SavesState {
  const [saves, setSaves] = useState<SaveSummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [deletingId, setDeletingId] = useState<string | null>(null);
  const [removeError, setRemoveError] = useState<string | null>(null);
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
        if (isAbort(failure)) {
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

  const remove = useCallback(async (saveId: string): Promise<boolean> => {
    setDeletingId(saveId);
    setRemoveError(null);
    try {
      await deleteSave(saveId);
      setSaves((current) => current.filter((row) => row.saveId !== saveId));
      return true;
    } catch (failure) {
      // The failure must be loud and the list must reflect the true state:
      // a 500 often means the save is damaged and must reappear as such.
      setRemoveError(apiErrorMessage(failure));
      setRevision((value) => value + 1);
      return false;
    } finally {
      setDeletingId(null);
    }
  }, []);

  const clearRemoveError = useCallback(() => {
    setRemoveError(null);
  }, []);

  return { saves, loading, error, deletingId, removeError, refresh, remove, addCreated, clearRemoveError };
}
