import { useEffect, useState } from 'react';
import { ApiError, apiErrorMessage, isAbort } from './api/http';

export interface SaveDataState<T> {
  data: T | null;
  loading: boolean;
  error: string | null;
  refresh: () => void;
}

/**
 * One shared save-data fetch hook (MSS-154). The pages previously kept four
 * drifted near-copies; only one had the out-of-order guard, so a promise
 * resolving just after a save/scope change could overwrite newer state with
 * stale data or kill the new spinner. Contract kept from the copies:
 *
 * - `key` scopes the request; `null` clears data and fetches nothing.
 * - 404 renders as empty data, never an error screen.
 * - Aborts (key change, unmount or refresh) never touch state.
 * - An already-resolved promise landing after the cleanup must be ignored:
 *   the out-of-order `signal.aborted` guard below is the stale-data fix.
 * - `refresh()` re-runs the same key once, for post-mutation reloads.
 */
export function useSaveData<T>(
  key: string | null,
  load: (signal: AbortSignal) => Promise<T>,
): SaveDataState<T> {
  const [data, setData] = useState<T | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [revision, setRevision] = useState(0);

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
        if (signal.aborted || isAbort(failure)) {
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
    // load is stable per key via inline closure over ids; re-run on key
    // change only, plus explicit refreshes.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key, revision]);

  return { data, loading, error, refresh: () => setRevision((value) => value + 1) };
}
