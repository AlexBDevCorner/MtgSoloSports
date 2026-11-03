import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { create, act } from 'react-test-renderer';
import { SavesPage } from './SavesPage.tsx';
import { MAX_SAFE_SEED_VALUE, parseDeterministicField } from './seedInput.ts';
import { createSave } from './savesApi.ts';

// MSS-130: entering a large seed/stream must never silently round to a
// different universe. The form transports seed/stream as JSON numbers, so it
// only supports exact doubles: blank (random) or 0..9007199254740991.
// Larger ulong values must be rejected with a field-related error and must
// never submit a rounded payload.

(globalThis as unknown as { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
(globalThis as unknown as { React: typeof React }).React = React;

function okJson(data: unknown): unknown {
  return {
    ok: true,
    status: 200,
    statusText: 'OK',
    json: () => Promise.resolve(data),
    text: () => Promise.resolve(JSON.stringify(data)),
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

function installCreateSaveFetch(captured: { bodies: string[] }): void {
  const g = globalThis as unknown as Record<string, unknown>;
  g['fetch'] = (input: unknown, init?: { method?: string; body?: string }) => {
    const url = String(input);
    const method = (init?.method ?? 'GET').toUpperCase();
    if (url === '/api/saves' && method === 'POST') {
      captured.bodies.push(String((init as { body?: string })?.body ?? ''));
      return Promise.resolve(
        okJson({
          saveId: '11111111-1111-1111-1111-111111111111',
          name: 'test',
          createdUtc: new Date(0).toISOString(),
          schemaVersion: 1,
          currentSeason: 1,
          phase: 'Preseason',
          totalAthletes: 2048,
          athletesPerColor: {},
          universeChecksum: 'chk',
        }),
      );
    }
    return Promise.resolve(okJson({}));
  };
}

function renderedText(renderer: ReturnType<typeof create>): string {
  return JSON.stringify((renderer as unknown as { toJSON: () => unknown }).toJSON());
}

type SavesPageRenderer = ReturnType<typeof create>;

async function mountSavesPage(): Promise<{ renderer: SavesPageRenderer; opened: string[] }> {
  const opened: string[] = [];
  let renderer: SavesPageRenderer | null = null;
  await act(async () => {
    renderer = create(
      React.createElement(SavesPage, {
        saves: {
          saves: [],
          loading: false,
          error: null,
          deletingId: null,
          refresh: () => undefined,
          remove: () => Promise.resolve(),
          addCreated: () => undefined,
        },
        activeSaveId: null,
        onOpenSave: (saveId: string) => {
          opened.push(saveId);
        },
        catalogSufficient: true,
        catalogStats: null,
        onCatalogImported: () => undefined,
      }),
    );
  });
  assert.ok(renderer);
  return { renderer, opened };
}

function findInputs(renderer: SavesPageRenderer): {
  name: { props: Record<string, unknown> };
  seed: { props: Record<string, unknown> };
  stream: { props: Record<string, unknown> };
} {
  const root = (renderer as unknown as { root: { findAllByType: (t: string) => Array<{ props: Record<string, unknown> }> } }).root;
  const inputs = root.findAllByType('input');
  const name = inputs.find((n) => n.props['placeholder'] === 'My sporting universe');
  const blanks = inputs.filter((n) => n.props['placeholder'] === 'Blank = random');
  assert.ok(name, 'name input renders');
  assert.equal(blanks.length, 2, `expected seed+stream inputs, got ${blanks.length}`);
  return { name, seed: blanks[0]!, stream: blanks[1]! };
}

function findCreateForm(renderer: SavesPageRenderer): { props: Record<string, unknown> } {
  const root = (renderer as unknown as { root: { findAllByType: (t: string) => Array<{ props: Record<string, unknown> }> } }).root;
  const forms = root.findAllByType('form');
  assert.ok(forms.length >= 1, 'create form renders');
  return forms[0]!;
}

async function setInputValue(renderer: SavesPageRenderer, kind: 'name' | 'seed' | 'stream', value: string): Promise<void> {
  await act(async () => {
    const current = findInputs(renderer)[kind];
    (current.props['onChange'] as (event: unknown) => void)({ target: { value } });
  });
}

async function submitCreateForm(renderer: SavesPageRenderer): Promise<void> {
  await act(async () => {
    const form = findCreateForm(renderer);
    (form.props['onSubmit'] as (event: unknown) => void)({ preventDefault: () => undefined });
    for (let i = 0; i < 12; i += 1) {
      await new Promise((resolve) => setTimeout(resolve, 0));
    }
  });
}

async function unmountRenderer(renderer: SavesPageRenderer): Promise<void> {
  await act(async () => {
    (renderer as unknown as { unmount: () => void }).unmount();
  });
}

describe('MSS-130 deterministic seed/stream precision', () => {
  beforeEach(() => {
    installGlobals();
  });

  afterEach(() => {
    const g = globalThis as unknown as Record<string, unknown>;
    delete g['fetch'];
  });

  it('documents the JavaScript rounding defect without relying on it as coverage', () => {
    // Number('9007199254740993') rounds to 9007199254740992; the regression
    // coverage below goes through the mounted form, not this illustration.
    assert.equal(Number('9007199254740993'), 9007199254740992);
    assert.equal(Number.MAX_SAFE_INTEGER, 9007199254740991);
    assert.equal(MAX_SAFE_SEED_VALUE, 9007199254740991);
  });

  it('parses blank and ordinary integers exactly for both fields', () => {
    for (const field of ['Seed', 'Stream'] as const) {
      assert.equal(parseDeterministicField('', field), undefined);
      assert.equal(parseDeterministicField('   ', field), undefined);
      assert.equal(parseDeterministicField('0', field), 0);
      assert.equal(parseDeterministicField('42', field), 42);
      assert.equal(parseDeterministicField('  7  ', field), 7);
      assert.equal(parseDeterministicField('9007199254740991', field), 9007199254740991);
    }
  });

  it('rejects unsafe integers for both fields with a field-related range error', () => {
    for (const field of ['Seed', 'Stream'] as const) {
      for (const raw of ['9007199254740992', '9007199254740993', '18446744073709551615']) {
        assert.throws(() => parseDeterministicField(raw, field), (error: unknown) => {
          assert.ok(error instanceof Error);
          assert.ok(error.message.includes(field), `error names ${field}: ${error.message}`);
          assert.ok(error.message.includes('9007199254740991'), `error documents range: ${error.message}`);
          return true;
        });
      }
    }
  });

  it('rejects negative, fractional, and non-finite input for both fields', () => {
    for (const field of ['Seed', 'Stream'] as const) {
      for (const raw of ['-1', '-0', '1.5', '42.0', 'Infinity', 'NaN', 'abc', '1e3']) {
        assert.throws(() => parseDeterministicField(raw, field), (error: unknown) => {
          assert.ok(error instanceof Error);
          assert.ok(error.message.includes(field), `error names ${field}: ${error.message}`);
          return true;
        });
      }
    }
  });

  it('serializes safe integers exactly and omits blanks', async () => {
    const captured = { bodies: [] as string[] };
    installCreateSaveFetch(captured);
    await createSave('exact', 9007199254740991, 42);
    assert.equal(captured.bodies.length, 1);
    const body = JSON.parse(captured.bodies[0]!) as Record<string, unknown>;
    assert.equal(body['seed'], 9007199254740991);
    assert.equal(body['stream'], 42);
    assert.equal(JSON.stringify(body).includes('9007199254740991'), true);

    captured.bodies.length = 0;
    await createSave('random');
    assert.equal(captured.bodies.length, 1);
    const blank = JSON.parse(captured.bodies[0]!) as Record<string, unknown>;
    assert.equal('seed' in blank, false);
    assert.equal('stream' in blank, false);
  });

  it('mounted form submits the safe maximum exactly for seed and stream', async () => {
    for (const field of ['seed', 'stream'] as const) {
      const captured = { bodies: [] as string[] };
      installCreateSaveFetch(captured);
      const { renderer, opened } = await mountSavesPage();
      try {
        await setInputValue(renderer, 'name', 'Precise');
        await setInputValue(renderer, field, '9007199254740991');
        await submitCreateForm(renderer);
        assert.equal(captured.bodies.length, 1, `${field} safe max submits once`);
        const body = JSON.parse(captured.bodies[0]!) as Record<string, unknown>;
        assert.equal(body[field], 9007199254740991);
        assert.equal(opened.length, 1);
        const text = renderedText(renderer);
        assert.ok(!text.includes('Could not create save'), `no error for safe ${field} max`);
      } finally {
        await unmountRenderer(renderer);
      }
    }
  });

  it('mounted form submits blank and ordinary integers without seed/stream rounding', async () => {
    const captured = { bodies: [] as string[] };
    installCreateSaveFetch(captured);
    const { renderer } = await mountSavesPage();
    try {
      await setInputValue(renderer, 'name', 'Ordinary');
      await setInputValue(renderer, 'seed', '42');
      await setInputValue(renderer, 'stream', '7');
      await submitCreateForm(renderer);
      assert.equal(captured.bodies.length, 1);
      const body = JSON.parse(captured.bodies[0]!) as Record<string, unknown>;
      assert.equal(body['seed'], 42);
      assert.equal(body['stream'], 7);
    } finally {
      await unmountRenderer(renderer);
    }

    const blankCapture = { bodies: [] as string[] };
    installCreateSaveFetch(blankCapture);
    const blankMount = await mountSavesPage();
    try {
      await setInputValue(blankMount.renderer, 'name', 'Random');
      await setInputValue(blankMount.renderer, 'seed', '');
      await setInputValue(blankMount.renderer, 'stream', '   ');
      await submitCreateForm(blankMount.renderer);
      assert.equal(blankCapture.bodies.length, 1);
      const blankBody = JSON.parse(blankCapture.bodies[0]!) as Record<string, unknown>;
      assert.equal('seed' in blankBody, false);
      assert.equal('stream' in blankBody, false);
    } finally {
      await unmountRenderer(blankMount.renderer);
    }
  });

  it('mounted form never submits 9007199254740992 for 9007199254740992/9007199254740993/2^64-1 in either field', async () => {
    const unsafe = ['9007199254740992', '9007199254740993', '18446744073709551615'];
    for (const field of ['seed', 'stream'] as const) {
      const fieldLabel = field === 'seed' ? 'Seed' : 'Stream';
      for (const raw of unsafe) {
        const captured = { bodies: [] as string[] };
        installCreateSaveFetch(captured);
        const { renderer } = await mountSavesPage();
        try {
          await setInputValue(renderer, 'name', 'Universe');
          await setInputValue(renderer, field, raw);
          await submitCreateForm(renderer);
          assert.equal(captured.bodies.length, 0, `${fieldLabel} ${raw} sends no request`);
          for (const body of captured.bodies) {
            assert.ok(!body.includes('9007199254740992'), `never submits rounded 2^53 for ${fieldLabel} ${raw}`);
          }
          const text = renderedText(renderer);
          assert.ok(text.includes('Could not create save'), `unsafe ${fieldLabel} ${raw} surfaces an error`);
          assert.ok(text.includes(fieldLabel), `unsafe ${fieldLabel} ${raw} error names the field`);
        } finally {
          await unmountRenderer(renderer);
        }
      }
    }
  });
});
