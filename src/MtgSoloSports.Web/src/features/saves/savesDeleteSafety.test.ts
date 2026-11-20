import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { create, act } from 'react-test-renderer';
import { SavesPage } from './SavesPage.tsx';
import { deleteSave } from './savesApi.ts';
import { useSaves, type SavesState } from './useSaves.ts';
import { SELECTED_SAVE_KEY, writeLastSelectedSave } from '../routing/router.tsx';
import { ApiError } from '../../shared/api/http.ts';

// MSS-145: deleting a save is a destructive, confirm-gated action whose
// failures must be loud (ApiError status + message, refreshed list) and a
// save that exists on disk but cannot be read stays visible as a damaged
// entry with delete/restore actions instead of silently vanishing.

(globalThis as unknown as { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
(globalThis as unknown as { React: typeof React }).React = React;

type Renderer = ReturnType<typeof create>;

const HEALTHY = {
  saveId: '11111111-1111-1111-1111-111111111111',
  name: 'Healthy Universe',
  createdUtc: '2026-01-02T03:04:05.000Z',
  schemaVersion: 3,
  currentSeason: 2,
  phase: 'SeasonInProgress',
};

const DAMAGED = {
  saveId: '22222222-2222-2222-2222-222222222222',
  name: '22222222222222222222222222222222.db',
  createdUtc: '0001-01-01T00:00:00.000Z',
  schemaVersion: 0,
  currentSeason: 0,
  phase: '',
  unreadable: true,
  unreadableReason: 'The save database could not be opened.',
};

const CHECKPOINT_ID = '33333333-3333-3333-3333-333333333333';

function okJson(data: unknown): unknown {
  return {
    ok: true,
    status: 200,
    statusText: 'OK',
    json: () => Promise.resolve(data),
    text: () => Promise.resolve(JSON.stringify(data)),
  };
}

function errJson(status: number, body: string): unknown {
  return {
    ok: false,
    status,
    statusText: 'Error',
    json: () => Promise.resolve({}),
    text: () => Promise.resolve(body),
  };
}

function installGlobals(): void {
  const g = globalThis as unknown as Record<string, unknown>;
  if (!g['localStorage']) {
    const store = new Map<string, string>();
    g['localStorage'] = {
      getItem: (k: string) => (store.has(k) ? (store.get(k) as string) : null),
      setItem: (k: string, v: string) => {
        store.set(k, v);
      },
      removeItem: (k: string) => {
        store.delete(k);
      },
      clear: () => {
        store.clear();
      },
    };
  } else {
    (g['localStorage'] as { clear: () => void }).clear();
  }
  if (!g['window']) {
    (g['window'] as unknown) = g;
  }
  const w = g['window'] as unknown as Record<string, unknown>;
  w['matchMedia'] ??= () => ({
    matches: false,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
  });
}

async function flush(rounds = 8): Promise<void> {
  for (let i = 0; i < rounds; i += 1) {
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
  }
}

function textOf(renderer: Renderer): string {
  return JSON.stringify((renderer as unknown as { toJSON: () => unknown }).toJSON());
}

function buttonsOf(renderer: Renderer): Array<{ props: Record<string, unknown> }> {
  const root = (renderer as unknown as { root: { findAllByType: (t: unknown) => Array<{ props: Record<string, unknown> }> } }).root;
  return root.findAllByType('button');
}

function findButton(renderer: Renderer, text: string): { props: Record<string, unknown> } {
  const found = buttonsOf(renderer).find((button) => String(button.props['children']) === text);
  assert.ok(found, `button "${text}" renders`);
  return found;
}

function fakeSavesState(overrides: Partial<SavesState> = {}): SavesState {
  return {
    saves: [],
    loading: false,
    error: null,
    deletingId: null,
    removeError: null,
    refresh: () => undefined,
    remove: () => Promise.resolve(true),
    addCreated: () => undefined,
    clearRemoveError: () => undefined,
    ...overrides,
  };
}

async function mountPage(saves: SavesState, activeSaveId: string | null): Promise<Renderer> {
  let renderer: Renderer | null = null;
  await act(async () => {
    renderer = create(
      React.createElement(SavesPage, {
        saves,
        activeSaveId,
        onOpenSave: () => undefined,
        catalogSufficient: true,
        catalogStats: null,
        onCatalogImported: () => undefined,
      }),
    );
  });
  assert.ok(renderer);
  return renderer;
}

async function unmount(renderer: Renderer): Promise<void> {
  await act(async () => {
    (renderer as unknown as { unmount: () => void }).unmount();
  });
}

async function click(renderer: Renderer, text: string): Promise<void> {
  await act(async () => {
    (findButton(renderer, text).props['onClick'] as () => void)();
  });
}

describe('MSS-145 safe save deletion', () => {
  beforeEach(() => {
    installGlobals();
  });

  afterEach(() => {
    const g = globalThis as unknown as Record<string, unknown>;
    delete g['fetch'];
  });

  it('deleteSave surfaces failures as ApiError with status and server message', async () => {
    const g = globalThis as unknown as Record<string, unknown>;
    const calls: string[] = [];
    g['fetch'] = (input: unknown, init?: { method?: string }) => {
      calls.push(`${(init?.method ?? 'GET').toUpperCase()} ${String(input)}`);
      return Promise.resolve({
        ok: false,
        status: 409,
        statusText: 'Conflict',
        json: () => Promise.resolve({}),
        text: () => Promise.resolve(JSON.stringify({ error: 'The save is busy.' })),
      });
    };

    await assert.rejects(deleteSave('abc'), (failure: unknown) => {
      assert.ok(failure instanceof ApiError, 'delete failures throw ApiError, not a plain Error');
      assert.equal((failure as ApiError).status, 409);
      assert.ok((failure as ApiError).body.includes('The save is busy.'));
      return true;
    });
    assert.deepEqual(calls, ['DELETE /api/saves/abc']);
  });

  it('deleteSave resolves on a successful response', async () => {
    const g = globalThis as unknown as Record<string, unknown>;
    g['fetch'] = () =>
      Promise.resolve({
        ok: true,
        status: 204,
        statusText: 'No Content',
        json: () => Promise.resolve(undefined),
        text: () => Promise.resolve(''),
      });
    await deleteSave('abc');
  });

  it('requires a second explicit click before deleting; Keep cancels and keeps the save', async () => {
    const removed: string[] = [];
    const state = fakeSavesState({
      saves: [HEALTHY],
      remove: (saveId: string) => {
        removed.push(saveId);
        return Promise.resolve(true);
      },
    });
    const renderer = await mountPage(state, null);
    try {
      // First click only arms the confirmation.
      await click(renderer, 'Delete');
      assert.equal(removed.length, 0, 'no delete request yet');
      let text = textOf(renderer);
      assert.ok(text.includes('This cannot be undone'), 'confirm copy explains the danger');
      assert.ok(text.includes('Yes, delete') && text.includes('Keep'), 'confirm offers both choices');

      // Keep cancels without any deletion.
      await click(renderer, 'Keep');
      assert.equal(removed.length, 0, 'cancel still deletes nothing');
      text = textOf(renderer);
      assert.ok(!text.includes('This cannot be undone'), 'confirm disappears on cancel');

      // Arm again, then confirm: exactly one delete for the right save.
      await click(renderer, 'Delete');
      await click(renderer, 'Yes, delete');
      await flush(4);
      assert.deepEqual(removed, [HEALTHY.saveId], 'one confirmed delete for the armed save');
      assert.ok(!textOf(renderer).includes('This cannot be undone'), 'confirm closes after confirming');
    } finally {
      await unmount(renderer);
    }
  });

  it('clears the last-selected save only when the delete actually succeeded', async () => {
    const outcomes = [false, true];
    let outcomeIndex = 0;
    const state = fakeSavesState({
      saves: [HEALTHY],
      remove: () => Promise.resolve(outcomes[outcomeIndex++] ?? true),
    });
    writeLastSelectedSave(HEALTHY.saveId);
    const renderer = await mountPage(state, HEALTHY.saveId);
    try {
      // Failed delete: the save stays selected because it still exists.
      await click(renderer, 'Delete');
      await click(renderer, 'Yes, delete');
      await flush(2);
      assert.equal(localStorage.getItem(SELECTED_SAVE_KEY), HEALTHY.saveId, 'failed delete keeps the selection');

      // Successful delete of the active save clears the stored selection.
      await click(renderer, 'Delete');
      await click(renderer, 'Yes, delete');
      await flush(2);
      assert.equal(localStorage.getItem(SELECTED_SAVE_KEY), null, 'successful delete clears the selection');
    } finally {
      await unmount(renderer);
    }
  });

  it('a failed delete exposes the error and refreshes the list so the true state is visible', async () => {
    const g = globalThis as unknown as Record<string, unknown>;
    const getCalls: string[] = [];
    let deleteCalls = 0;
    g['fetch'] = (input: unknown, init?: { method?: string }) => {
      const url = String(input);
      const method = (init?.method ?? 'GET').toUpperCase();
      if (url === '/api/saves' && method === 'GET') {
        getCalls.push(url);
        return Promise.resolve(okJson({ saves: [HEALTHY] }));
      }
      if (url === `/api/saves/${HEALTHY.saveId}` && method === 'DELETE') {
        deleteCalls += 1;
        return Promise.resolve(errJson(500, JSON.stringify({ error: 'The save database is damaged.' })));
      }
      return Promise.resolve(errJson(404, 'not found'));
    };

    let holder: { state: SavesState | null } = { state: null };
    function HookProbe(): React.ReactElement {
      const state = useSaves();
      holder.state = state;
      return React.createElement(
        'div',
        null,
        state.loading ? 'loading' : 'idle',
        '|',
        state.removeError ?? 'noRemoveError',
        '|',
        state.saves.map((row) => row.saveId).join(','),
      );
    }

    let renderer: Renderer | null = null;
    await act(async () => {
      renderer = create(React.createElement(HookProbe));
    });
    assert.ok(renderer);
    await flush(4);
    assert.equal(getCalls.length, 1, 'initial list load');

    let deleted = true;
    await act(async () => {
      deleted = await (holder.state as SavesState).remove(HEALTHY.saveId);
    });
    await flush(6);

    assert.equal(deleted, false, 'remove reports the failure instead of silently resolving');
    assert.equal(deleteCalls, 1, 'exactly one delete request');
    assert.equal(getCalls.length, 2, 'the list is refreshed after the failure');
    assert.ok((holder.state as SavesState).removeError?.includes('The save database is damaged.'));
    assert.ok(
      (holder.state as SavesState).saves.some((row) => row.saveId === HEALTHY.saveId),
      'the save stays listed in its true state',
    );
    const probeText = textOf(renderer as Renderer);
    assert.ok(probeText.includes('The save database is damaged.'), 'hook exposes the error message');

    // The exposed error is dismissable and never blocks a later successful delete.
    await act(async () => {
      (holder.state as SavesState).clearRemoveError();
    });
    assert.equal((holder.state as SavesState).removeError, null);

    await act(async () => {
      (renderer as unknown as { unmount: () => void }).unmount();
    });
  });

  it('a successful delete removes the row and leaves no error state', async () => {
    const g = globalThis as unknown as Record<string, unknown>;
    g['fetch'] = (input: unknown, init?: { method?: string }) => {
      const url = String(input);
      const method = (init?.method ?? 'GET').toUpperCase();
      if (url === '/api/saves' && method === 'GET') {
        return Promise.resolve(okJson({ saves: [HEALTHY] }));
      }
      if (url === `/api/saves/${HEALTHY.saveId}` && method === 'DELETE') {
        return Promise.resolve({ ok: true, status: 204, statusText: 'No Content' });
      }
      return Promise.resolve(errJson(404, 'not found'));
    };

    let holder: { state: SavesState | null } = { state: null };
    function HookProbe(): React.ReactElement {
      const state = useSaves();
      holder.state = state;
      return React.createElement('div', null, state.saves.map((row) => row.saveId).join(','));
    }

    let renderer: Renderer | null = null;
    await act(async () => {
      renderer = create(React.createElement(HookProbe));
    });
    await flush(4);

    let deleted = false;
    await act(async () => {
      deleted = await (holder.state as SavesState).remove(HEALTHY.saveId);
    });
    await flush(2);
    assert.equal(deleted, true);
    assert.equal((holder.state as SavesState).removeError, null);
    assert.equal((holder.state as SavesState).saves.length, 0, 'row is gone after the confirmed delete');
    await act(async () => {
      (renderer as unknown as { unmount: () => void }).unmount();
    });
  });
});

describe('MSS-145 damaged saves stay visible', () => {
  beforeEach(() => {
    installGlobals();
  });

  afterEach(() => {
    const g = globalThis as unknown as Record<string, unknown>;
    delete g['fetch'];
  });

  function installCheckpointFetch(captured: {
    listCalls: string[];
    restoreCalls: string[];
    checkpointBody: () => unknown;
    restoreFailure?: () => unknown;
  }): void {
    const g = globalThis as unknown as Record<string, unknown>;
    g['fetch'] = (input: unknown, init?: { method?: string }) => {
      const url = String(input);
      const method = (init?.method ?? 'GET').toUpperCase();
      if (url === `/api/saves/${DAMAGED.saveId}/checkpoints` && method === 'GET') {
        captured.listCalls.push(url);
        return Promise.resolve(okJson(captured.checkpointBody()));
      }
      if (url.includes('/restore') && method === 'POST') {
        captured.restoreCalls.push(url);
        const failure = captured.restoreFailure?.();
        if (failure) {
          return Promise.resolve(failure);
        }
        return Promise.resolve(okJson({ saveId: DAMAGED.saveId }));
      }
      return Promise.resolve(errJson(404, 'not found'));
    };
  }

  it('renders a damaged entry with reason, no open action, delete, and checkpoint restore', async () => {
    const captured = { listCalls: [] as string[], restoreCalls: [] as string[], checkpointBody: () => ({ checkpoints: [{ checkpointId: CHECKPOINT_ID }] }) };
    installCheckpointFetch(captured);
    const refreshes: number[] = [];
    const state = fakeSavesState({
      saves: [HEALTHY, DAMAGED],
      refresh: () => {
        refreshes.push(refreshes.length);
      },
    });
    const renderer = await mountPage(state, null);
    try {
      let text = textOf(renderer);
      assert.ok(text.includes(DAMAGED.unreadableReason as string), 'damaged entry shows its short reason');
      assert.ok(text.includes('Damaged'), 'damaged entry carries the Damaged badge');
      assert.ok(text.includes(DAMAGED.name), 'damaged entry is identified by its file name');
      assert.ok(text.includes(`/saves/${HEALTHY.saveId}/dashboard`), 'healthy entry keeps its open action');
      assert.ok(!text.includes(`/saves/${DAMAGED.saveId}/dashboard`), 'damaged entry has no open action');
      assert.ok(
        !text.includes(`/api/saves/${DAMAGED.saveId}/export`),
        'damaged entry offers no export of unreadable bytes',
      );

      // The damaged row still offers deletion behind the same confirmation.
      await click(renderer, 'Delete');
      assert.ok(textOf(renderer).includes('This cannot be undone'), 'damaged entry delete is confirm-gated too');

      // A recovery checkpoint is offered only after the lazy list proves one exists.
      await flush(4);
      assert.equal(captured.listCalls.length, 1, 'checkpoints are fetched once for the damaged row');
      text = textOf(renderer);
      assert.ok(text.includes('Restore checkpoint'), 'restore is offered when a checkpoint exists');
      await click(renderer, 'Restore checkpoint');
      await flush(4);
      assert.equal(captured.restoreCalls.length, 1, 'restore posts to the checkpoint restore route');
      assert.equal(captured.restoreCalls[0], `/api/saves/${DAMAGED.saveId}/checkpoints/${CHECKPOINT_ID}/restore`);
      assert.equal(refreshes.length, 1, 'a completed restore refreshes the saves list');
    } finally {
      await unmount(renderer);
    }
  });

  it('offers no restore when the damaged save has no recovery checkpoint', async () => {
    const captured = { listCalls: [] as string[], restoreCalls: [] as string[], checkpointBody: () => ({ checkpoints: [] }) };
    installCheckpointFetch(captured);
    const state = fakeSavesState({ saves: [DAMAGED] });
    const renderer = await mountPage(state, null);
    try {
      await flush(4);
      assert.equal(captured.listCalls.length, 1);
      const text = textOf(renderer);
      assert.ok(!text.includes('Restore checkpoint'), 'nothing to restore is nothing offered');
      assert.ok(text.includes('Damaged'), 'the damaged entry itself stays visible');
    } finally {
      await unmount(renderer);
    }
  });

  it('surfaces a failed restore inline instead of swallowing it', async () => {
    const captured = {
      listCalls: [] as string[],
      restoreCalls: [] as string[],
      checkpointBody: () => ({ checkpoints: [{ checkpointId: CHECKPOINT_ID }] }),
      restoreFailure: () => errJson(500, JSON.stringify({ error: 'Checkpoint verification failed.' })),
    };
    installCheckpointFetch(captured);
    const refreshes: string[] = [];
    const state = fakeSavesState({
      saves: [DAMAGED],
      refresh: () => {
        refreshes.push('refresh');
      },
    });
    const renderer = await mountPage(state, null);
    try {
      await flush(4);
      await click(renderer, 'Restore checkpoint');
      await flush(4);
      const text = textOf(renderer);
      assert.ok(text.includes('Checkpoint verification failed.'), 'restore failure is visible in the row');
      assert.equal(refreshes.length, 0, 'no refresh claims a restore that did not happen');
    } finally {
      await unmount(renderer);
    }
  });
});
