import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { create, act } from 'react-test-renderer';
import { ReportsPage } from './ReportsPage';
import type { DriveStatus } from './drivePublishingApi';
import type { HistorySeasons } from '../history/historyApi';

// MSS-155: mounts the real ReportsPage through loading -> populated /
// error / empty transitions and pins exact labels, field values, notices and
// busy gating. The page is a thin composition of per-section components
// (downloads, Drive publishing, adoptions, actions, auto-publish); these
// assertions document that the decomposition is structure-only: DOM output
// for the same data must stay equivalent.
//
// contentscript.js note: see the standings regression file —
// `contentscript.js` / `MaxListenersExceededWarning` / `ObjectMultiplex`
// warnings come from an injected browser/extension content script, not from
// application code. No `contentscript.js` exists in this repository.

(globalThis as unknown as { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
// tsx compiles page JSX with the classic React.createElement transform in the
// Node test runner (Vite uses the automatic runtime). Expose React globally
// so the real page modules render without a build-time transform change.
(globalThis as unknown as { React: typeof React }).React = React;

const SAVE = 'report-save';

function seasonsTwo(): HistorySeasons {
  return {
    saveId: SAVE,
    seasons: [
      {
        seasonNumber: 1,
        hasSuperleague: true,
        isComplete: true,
        competitionCount: 5,
        completedStages: 32,
        totalRounds: 32,
      },
      {
        seasonNumber: 2,
        hasSuperleague: true,
        isComplete: false,
        competitionCount: 5,
        completedStages: 3,
        totalRounds: 32,
      },
    ],
  };
}

function driveStatusConnected(): DriveStatus {
  return {
    saveId: SAVE,
    saveName: 'Report save',
    currentSeason: 2,
    phase: 'Regular',
    configured: true,
    connected: true,
    scopes: 'https://www.googleapis.com/auth/drive.file',
    connectedUtc: '2026-01-01T00:00:00Z',
    dossier: {
      fileId: 'dossier-9',
      fileName: 'season-2-dossier.md',
      fingerprint: null,
      remoteModifiedTime: null,
      lastOutcome: 'updated',
      webViewLink: null,
    },
    almanac: {
      fileId: 'almanac-9',
      fileName: 'universe-almanac-2.md',
      fingerprint: null,
      remoteModifiedTime: null,
      lastOutcome: 'created',
      webViewLink: null,
    },
    mtshistoryFolderId: 'folder-root',
    mtshistoryFolderName: 'mtshistory',
    seasonsFolderId: 'folder-seasons',
    lastPublishedUtc: '2026-03-03T10:00:00Z',
    lastSeasonNumber: 2,
    lastPhase: 'Regular',
    lastGlobalStage: 4,
    lastError: null,
    pickerClientId: 'client-1',
    pickerApiKey: 'key-1',
    pickerAppId: 'app-1',
    pickerEnabled: true,
    autoPublishEnabled: true,
    autoPublishStatus: 'Pending',
    autoRequestedSeason: 2,
    autoRequestedGlobalStage: 4,
    autoRequestedPhase: 'Regular',
    autoRequestedUtc: '2026-03-03T09:00:00Z',
    autoAttemptCount: 1,
    autoNextRetryUtc: null,
    autoLastError: 'auto boom',
    autoPausedForAuth: false,
    colorCupFolderId: 'color-cup-1',
    colorCupsFolderId: 'color-cups-1',
    colorTeamsFolderId: 'color-teams-1',
    typeCupFolderId: 'type-cup-1',
    typeCupsFolderId: 'type-cups-1',
    typeTeamsFolderId: 'type-teams-1',
    chaosCupFolderId: 'chaos-cup-1',
    chaosCupsFolderId: 'chaos-cups-1',
    lastLibraryPublishedUtc: '2026-04-04T00:00:00Z',
    lastLibraryError: null,
    lastLibraryCreated: 1,
    lastLibraryUpdated: 2,
    lastLibraryUnchanged: 3,
    lastLibraryFailed: 4,
    lastLibraryByCategory: null,
    athletesFolderId: 'athletes-1',
    athletesFolderName: 'Athletes',
    athleteColorFolderIds: null,
    athletesIndexFileId: 'athletes-index-1',
    athletesIndexFileName: 'athletes-index.md',
  };
}

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
    statusText: body,
    json: () => Promise.resolve({ error: body }),
    text: () => Promise.resolve(body),
  };
}

function installGlobals(): void {
  const g = globalThis as unknown as Record<string, unknown>;
  if (!g['window']) {
    (g['window'] as unknown) = g;
  }
  const w = g['window'] as unknown as Record<string, unknown>;
  if (!w['location']) {
    w['location'] = { pathname: `/saves/${SAVE}/reports`, search: '' };
  }
  if (!w['history']) {
    w['history'] = { replaceState: () => undefined };
  }
  if (!w['setTimeout']) {
    w['setTimeout'] = setTimeout;
  }
  if (!w['clearTimeout']) {
    w['clearTimeout'] = clearTimeout;
  }
}

function installFetch(impl: (url: string) => unknown): void {
  const g = globalThis as unknown as Record<string, unknown>;
  g['fetch'] = (input: unknown) => {
    const url = String(input);
    try {
      const data = impl(url);
      if (data === null) {
        return Promise.resolve(errJson(404, 'not found'));
      }
      if (data instanceof Error) {
        return Promise.reject(data);
      }
      return Promise.resolve(okJson(data));
    } catch (failure) {
      return Promise.reject(failure);
    }
  };
}

async function flush(rounds = 10): Promise<void> {
  for (let i = 0; i < rounds; i += 1) {
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
  }
}

async function unmountReportsPage(renderer: { unmount: () => void }): Promise<void> {
  await act(async () => {
    renderer.unmount();
  });
}

async function mountReportsPage(): Promise<{
  toJSON: () => unknown;
  root: {
    findAll: (predicate: (node: { type: unknown }) => boolean) => Array<{
      props: Record<string, unknown>;
    }>;
  };
  unmount: () => void;
}> {
  let renderer: ReturnType<typeof create> | null = null;
  await act(async () => {
    renderer = create(React.createElement(ReportsPage, { saveId: SAVE }));
  });
  assert.ok(renderer);
  await flush();
  return renderer as unknown as {
    toJSON: () => unknown;
    root: {
      findAll: (predicate: (node: { type: unknown }) => boolean) => Array<{
        props: Record<string, unknown>;
      }>;
    };
    unmount: () => void;
  };
}

function populatedUrls(): (url: string) => unknown {
  return (url: string) => {
    if (url === `/api/saves/${SAVE}/history/seasons`) {
      return seasonsTwo();
    }
    if (url === `/api/saves/${SAVE}/reports/drive/status`) {
      return driveStatusConnected();
    }
    return null;
  };
}

describe('MSS-155 ReportsPage mounted sections', () => {
  beforeEach(() => {
    installGlobals();
  });

  afterEach(() => {
    const g = globalThis as unknown as Record<string, unknown>;
    delete g['fetch'];
  });

  it('renders the populated page with every section and no hook errors', async () => {
    installFetch(populatedUrls());
    const renderer = await mountReportsPage();
    const output = JSON.stringify(renderer.toJSON());
    assert.ok(output.length > 200, 'populated render is not blank');

    // Downloads section: season selection and download buttons.
    assert.ok(output.includes('Reports for ChatGPT'), 'intro card renders');
    assert.ok(output.includes('One season, journalism-ready'), 'season dossier card renders');
    assert.ok(output.includes('"children":["Season ","1"]'), 'completed season option renders');
    assert.ok(
      output.includes('"children":["Season ","2"," (in progress)"]'),
      'in-progress season option renders',
    );
    assert.ok(output.includes('Dossier .md'), 'dossier markdown button renders');
    assert.ok(output.includes('Dossier .json'), 'dossier json button renders');
    assert.ok(output.includes('All-time reference'), 'almanac card renders');
    assert.ok(output.includes('Almanac .md'), 'almanac markdown button renders');
    assert.ok(output.includes('Almanac .json'), 'almanac json button renders');
    assert.ok(!output.includes('No seasons yet to export.'), 'seasons exist so no empty hint');

    // Drive connection and destination fields adopted from status.
    assert.ok(output.includes('Publish to Drive'), 'drive card renders');
    assert.ok(output.includes('Server is configured for Google OAuth.'), 'configured note renders');
    assert.ok(
      output.includes('Connected (https://www.googleapis.com/auth/drive.file).'),
      'connected scopes note renders',
    );
    assert.ok(output.includes('Connect Google Drive'), 'connect button renders');
    assert.ok(output.includes('Disconnect'), 'disconnect button renders');
    assert.ok(output.includes('"value":"folder-root"'), 'mtshistory folder id adopted');
    assert.ok(output.includes('"value":"mtshistory"'), 'folder display name adopted');
    assert.ok(output.includes('"value":"folder-seasons"'), 'seasons folder id adopted');
    assert.ok(output.includes('"value":"dossier-9"'), 'dossier file id adopted');
    assert.ok(output.includes('"value":"season-2-dossier.md"'), 'dossier file name adopted');
    assert.ok(output.includes('"value":"almanac-9"'), 'almanac file id adopted');
    assert.ok(output.includes('"value":"color-cup-1"'), 'color cup folder adopted');
    assert.ok(output.includes('"value":"chaos-cups-1"'), 'chaos cups folder adopted');
    assert.ok(output.includes('"value":"athletes-1"'), 'athletes folder adopted');
    assert.ok(output.includes('"value":"athletes-index.md"'), 'athletes index name adopted');

    // Picker grant buttons (single picker control).
    assert.ok(output.includes('Choose folder…'), 'folder picker button renders');
    assert.ok(output.includes('Choose Seasons…'), 'seasons picker button renders');
    assert.ok(output.includes('Choose dossier…'), 'dossier picker button renders');
    assert.ok(output.includes('Choose almanac…'), 'almanac picker button renders');
    assert.ok(output.includes('Choose Color Cup…'), 'color cup picker button renders');
    assert.ok(output.includes('Choose Color Cups…'), 'color cups picker button renders');
    assert.ok(output.includes('Choose Color Teams…'), 'color teams picker button renders');
    assert.ok(output.includes('Choose Type Cup…'), 'type cup picker button renders');
    assert.ok(output.includes('Choose Type Cups…'), 'type cups picker button renders');
    assert.ok(output.includes('Choose Type Teams…'), 'type teams picker button renders');
    assert.ok(output.includes('Choose Chaos Cup…'), 'chaos cup picker button renders');
    assert.ok(output.includes('Choose Chaos Cups…'), 'chaos cups picker button renders');
    assert.ok(output.includes('Choose season file…'), 'season grant button renders');
    assert.ok(output.includes('Choose edition file…'), 'edition grant button renders');
    assert.ok(output.includes('Choose team file…'), 'team grant button renders');
    assert.ok(output.includes('Choose athlete file…'), 'athlete grant button renders');
    assert.ok(!output.includes('Opening Picker…'), 'no picker is busy at rest');

    // Adoptions, save and publish actions.
    assert.ok(output.includes('Historical dossier adoptions (JSON)'), 'dossier adoptions input renders');
    assert.ok(output.includes('Athlete adoptions (JSON)'), 'athlete adoptions input renders');
    assert.ok(output.includes('Season key to grant'), 'season key input renders');
    assert.ok(output.includes('Save destination'), 'save destination button renders');
    assert.ok(!output.includes('Saving…'), 'save button is not busy at rest');
    assert.ok(output.includes('Confirm cross-save use'), 'cross-save checkbox renders');
    assert.ok(output.includes('Overwrite independently edited Drive files'), 'overwrite checkbox renders');
    assert.ok(output.includes('Allow creating missing folders/files'), 'create-missing checkbox renders');
    assert.ok(output.includes('Verify access'), 'verify button renders');
    assert.ok(output.includes('Publish now'), 'publish button renders');
    assert.ok(output.includes('Preview library'), 'library preview button renders');
    assert.ok(output.includes('Sync all changed reports'), 'library sync button renders');
    assert.ok(output.includes('Preview athletes'), 'athlete preview button renders');
    assert.ok(
      output.includes('Publish all athletes / Sync changed athletes'),
      'athlete sync button renders',
    );

    // Status summaries and auto-publish area.
    assert.ok(output.includes('"folder athletes-1"'), 'athletes folder summary renders');
    assert.ok(output.includes('" · index athletes-index-1"'), 'athletes index summary renders');
    assert.ok(output.includes('"synced 2026-04-04T00:00:00Z"'), 'library synced summary renders');
    assert.ok(output.includes('" · +1 ~2 =3 !4"'), 'library counters summary renders');
    assert.ok(output.includes('"Last publish: season "'), 'last publish summary renders');
    assert.ok(output.includes('" · dossier "'), 'last publish dossier outcome label renders');
    assert.ok(output.includes('" · almanac"'), 'last publish almanac outcome label renders');
    assert.ok(output.includes('"updated"'), 'dossier outcome value renders');
    assert.ok(output.includes('"created"'), 'almanac outcome value renders');
    assert.ok(
      output.includes('Automatically publish after each completed stage'),
      'auto-publish checkbox renders',
    );
    assert.ok(output.includes('"Auto-publish: "'), 'auto-publish status prefix renders');
    assert.ok(output.includes('"Pending"'), 'auto-publish status value renders');
    assert.ok(
      output.includes('" · requested season 2 · stage 4"'),
      'auto-publish requested stage line renders',
    );
    assert.ok(output.includes('Retry auto-publish'), 'auto-publish retry button renders');
    assert.ok(output.includes('"Auto-publish note: "'), 'auto-publish note prefix renders');
    assert.ok(output.includes('"auto boom"'), 'auto-publish error note renders');
    assert.ok(!output.includes('Automatic publishing is paused'), 'not paused while connected');

    // Results stay empty until an action runs.
    assert.ok(!output.includes('Verify: season'), 'verify result list is hidden at rest');
    assert.ok(!output.includes('Library preview'), 'library result list is hidden at rest');

    // Workflow card.
    assert.ok(output.includes('How to brief ChatGPT'), 'workflow card renders');
    assert.ok(output.includes('You are a longtime MtgSoloSports columnist.'), 'prompt text renders');
    assert.ok(output.includes('Copy prompt'), 'copy button renders');
    await unmountReportsPage(renderer);
  });

  it('renders an empty seasons hint without an error notice on 404', async () => {
    installFetch(populatedUrls());
    const g = globalThis as unknown as { fetch: (input: unknown) => unknown; window: { location: unknown } };
    const realFetch = g.fetch;
    g.fetch = (input: unknown) => {
      const url = String(input);
      if (url === `/api/saves/${SAVE}/history/seasons`) {
        return Promise.resolve(errJson(404, 'no history yet'));
      }
      return realFetch(input);
    };
    const renderer = await mountReportsPage();
    const output = JSON.stringify(renderer.toJSON());
    assert.ok(output.includes('No seasons yet to export.'), 'empty export hint renders');
    assert.ok(!output.includes('Seasons unavailable'), '404 seasons stay quiet');
    assert.ok(output.includes('Publish to Drive'), 'drive section still renders');
    await unmountReportsPage(renderer);
  });

  it('surfaces a seasons error without blanking the page', async () => {
    installFetch(populatedUrls());
    const g = globalThis as unknown as { fetch: (input: unknown) => unknown };
    const realFetch = g.fetch;
    g.fetch = (input: unknown) => {
      const url = String(input);
      if (url === `/api/saves/${SAVE}/history/seasons`) {
        return Promise.reject(new Error('Network down.'));
      }
      return realFetch(input);
    };
    const renderer = await mountReportsPage();
    const output = JSON.stringify(renderer.toJSON());
    assert.ok(output.length > 50, 'error render is not blank');
    assert.ok(output.includes('Seasons unavailable'), 'seasons error notice renders');
    assert.ok(output.includes('Network down.'), 'seasons error detail renders');
    assert.ok(output.includes('No seasons yet to export.'), 'no season is selected after failure');
    await unmountReportsPage(renderer);
  });

  it('surfaces a Drive status error and keeps the download sections', async () => {
    installFetch(populatedUrls());
    const g = globalThis as unknown as { fetch: (input: unknown) => unknown };
    const realFetch = g.fetch;
    g.fetch = (input: unknown) => {
      const url = String(input);
      if (url === `/api/saves/${SAVE}/reports/drive/status`) {
        return Promise.reject(new Error('Drive down.'));
      }
      return realFetch(input);
    };
    const renderer = await mountReportsPage();
    const output = JSON.stringify(renderer.toJSON());
    assert.ok(output.includes('Drive status unavailable'), 'drive error notice renders');
    assert.ok(output.includes('Drive down.'), 'drive error detail renders');
    assert.ok(!output.includes('Connect Google Drive'), 'drive controls stay hidden without status');
    assert.ok(output.includes('Dossier .md'), 'download buttons still render');
    await unmountReportsPage(renderer);
  });

  it('clears and restores the season selection through the dossier select', async () => {
    installFetch(populatedUrls());
    const renderer = await mountReportsPage();
    const selects = renderer.root.findAll((node) => node.type === 'select');
    assert.equal(selects.length, 1, 'exactly one season select on the page');

    await act(async () => {
      (selects[0]!.props.onChange as (event: { target: { value: string } }) => void)({
        target: { value: 'not-a-number' },
      });
    });
    let output = JSON.stringify(renderer.toJSON());
    assert.ok(output.includes('No seasons yet to export.'), 'invalid select clears the season');

    await act(async () => {
      (selects[0]!.props.onChange as (event: { target: { value: string } }) => void)({
        target: { value: '1' },
      });
    });
    output = JSON.stringify(renderer.toJSON());
    assert.ok(!output.includes('No seasons yet to export.'), 'valid select restores the season');
    await unmountReportsPage(renderer);
  });
});
