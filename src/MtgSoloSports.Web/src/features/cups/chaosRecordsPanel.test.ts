import { describe, it, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { create, act } from 'react-test-renderer';
import { ChaosRecordsPanel } from './ChaosRecordsPanel.tsx';

// MSS-117 mounted coverage: factual Chaos records plus conditional,
// source-linked Tournament Stories. Empty states are valid; group outranking
// alone never qualifies as an underdog and stories never affect scoring.

(globalThis as unknown as { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
(globalThis as unknown as { React: typeof React }).React = React;

const SAVE = '55555555-5555-5555-5555-555555555555';

function okJson(data: unknown): unknown {
  return {
    ok: true,
    status: 200,
    statusText: 'OK',
    json: () => Promise.resolve(data),
    text: () => Promise.resolve(JSON.stringify(data)),
  };
}

function vacantRecords(): Record<string, unknown> {
  return {
    saveId: SAVE,
    thresholdsVersion: 'MSS-117-v1',
    completedSeasons: [],
    athletes: [],
    divisions: [],
    colors: [],
    stories: [],
  };
}

function storiedRecords(): Record<string, unknown> {
  return {
    saveId: SAVE,
    thresholdsVersion: 'MSS-117-v1',
    completedSeasons: [1, 2],
    athletes: [
      {
        saveAthleteId: 11,
        athleteName: 'Pool Hero',
        titles: 1,
        medals: 1,
        finals: 2,
        bestFinalRank: 5,
        deepestStage: 5,
        longestProgressStreak: 2,
        p16Survivals: 1,
      },
    ],
    divisions: [],
    colors: [],
    stories: [
      {
        kind: 'underdog',
        saveAthleteId: 11,
        athleteName: 'Pool Hero',
        sourceSeasonNumber: 2,
        relatedSeasonNumber: null,
        detail: 'Pool entrant Final P5.',
        text: 'Pool Hero reached Final P5 as a Pool entrant in Season 2.',
      },
      {
        kind: 'defense_failed',
        saveAthleteId: 12,
        athleteName: 'Champ',
        sourceSeasonNumber: 2,
        relatedSeasonNumber: 1,
        detail: 'Defending champion from Season 1 did not repeat in Season 2.',
        text: 'Champ failed to defend the Chaos Cup title in Season 2 after winning Season 1.',
      },
    ],
  };
}

function installRecordsFetch(payload: Record<string, unknown>): void {
  const g = globalThis as unknown as Record<string, unknown>;
  g['fetch'] = () => Promise.resolve(okJson(payload));
}

async function flush(rounds = 8): Promise<void> {
  for (let i = 0; i < rounds; i += 1) {
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
  }
}

function textOf(renderer: ReturnType<typeof create>): string {
  return JSON.stringify((renderer as unknown as { toJSON: () => unknown }).toJSON());
}

describe('MSS-117 Chaos records panel', () => {
  beforeEach(() => {
    installRecordsFetch(vacantRecords());
  });

  it('renders vacant records with an allowed empty story state', async () => {
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(React.createElement(ChaosRecordsPanel, { saveId: SAVE }));
    });
    await flush();
    const text = textOf(renderer!);
    assert.ok(text.includes('No Chaos history yet') || text.includes('No completed Chaos edition yet'), 'vacant copy');
    assert.ok(text.includes('No Tournament Stories'), 'empty stories allowed');
    assert.ok(!text.includes('head-to-head'), 'never invents a duel');
  });

  it('renders conditional source-linked story cards without sport effects', async () => {
    installRecordsFetch(storiedRecords());
    let renderer: ReturnType<typeof create> | null = null;
    await act(async () => {
      renderer = create(React.createElement(ChaosRecordsPanel, { saveId: SAVE }));
    });
    await flush();
    const text = textOf(renderer!);
    assert.ok(text.includes('Pool Hero'), 'underdog athlete shows');
    assert.ok(text.includes('Underdog'), 'underdog kind labelled');
    assert.ok(text.includes('Title defense failed'), 'defense kind labelled');
    assert.ok(text.includes('Season 2'), 'source season linked');
    const root = (renderer as unknown as { root: { findAllByType: (t: string) => Array<{ props: Record<string, unknown> }> } }).root;
    const anchors = root.findAllByType('a').map((anchor) => String(anchor.props['href'] ?? ''));
    assert.ok(anchors.some((href) => href.includes('/athletes/11')), 'story links to athlete profile');
    assert.ok(anchors.some((href) => href.includes('/cups/chaos/2')), 'story links to source edition');
    assert.ok(!text.includes('points') || !text.includes('+'), 'no invented points impact copy');
  });
});
