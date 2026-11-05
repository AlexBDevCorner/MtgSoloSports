import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  describeAutoPublishStatus,
  driveAutoPublishRetryUrl,
  driveAutoPublishUrl,
  driveAuthUrl,
  driveDestinationUrl,
  driveDisconnectUrl,
  driveLibraryPreviewUrl,
  driveLibrarySyncUrl,
  drivePublishUrl,
  driveStatusUrl,
  driveVerifyUrl,
  mergeLibraryAdoptionEntry,
} from './drivePublishingApi';

describe('MSS-089 Drive publishing URLs', () => {
  it('builds status, auth, disconnect, destination and publish URLs with exact save scope', () => {
    assert.equal(driveStatusUrl('save-1'), '/api/saves/save-1/reports/drive/status');
    assert.equal(driveAuthUrl('save-1'), '/api/saves/save-1/reports/drive/auth-url');
    assert.equal(driveDisconnectUrl('save-1'), '/api/saves/save-1/reports/drive/disconnect');
    assert.equal(driveDestinationUrl('save-1'), '/api/saves/save-1/reports/drive/destination');
    assert.equal(drivePublishUrl('save-1'), '/api/saves/save-1/reports/drive/publish');
    assert.equal(driveVerifyUrl('save-1'), '/api/saves/save-1/reports/drive/verify');
  });
});

describe('MSS-090 Drive auto-publish URLs and status labels', () => {
  it('builds auto-publish toggle and retry URLs with exact save scope', () => {
    assert.equal(driveAutoPublishUrl('save-1'), '/api/saves/save-1/reports/drive/auto-publish');
    assert.equal(
      driveAutoPublishRetryUrl('save-1'),
      '/api/saves/save-1/reports/drive/auto-publish/retry',
    );
  });

  it('labels auto-publish statuses without implying success', () => {
    assert.equal(describeAutoPublishStatus('UpToDate'), 'Up to date');
    assert.equal(describeAutoPublishStatus('Publishing'), 'Publishing');
    assert.equal(describeAutoPublishStatus('Pending'), 'Pending');
    assert.equal(describeAutoPublishStatus('Failed'), 'Failed');
    assert.equal(describeAutoPublishStatus('Disconnected'), 'Disconnected');
    assert.equal(describeAutoPublishStatus('Disabled'), 'Disabled');
    assert.equal(describeAutoPublishStatus('unknown'), 'Disabled');
  });
});

describe('MSS-091 Drive library sync URLs', () => {
  it('builds library preview and sync URLs with exact save scope', () => {
    assert.equal(driveLibraryPreviewUrl('save-1'), '/api/saves/save-1/reports/drive/library/preview');
    assert.equal(driveLibrarySyncUrl('save-1'), '/api/saves/save-1/reports/drive/library/sync');
  });
});

describe('MSS-091 library file grants', () => {
  it('merges one Picker grant into empty adoption JSON', () => {
    const merged = mergeLibraryAdoptionEntry('', '1', 'file-1', 'season-1-dossier.md');
    assert.equal(merged, JSON.stringify({ '1': { fileId: 'file-1', fileName: 'season-1-dossier.md' } }));
  });

  it('merges one Picker grant without losing existing adoptions', () => {
    const existing = JSON.stringify({ '1': { fileId: 'file-1', fileName: 'a.md' } });
    const merged = mergeLibraryAdoptionEntry(existing, 'color:1', 'file-2', 'my-edition.md');
    assert.equal(
      merged,
      JSON.stringify({
        '1': { fileId: 'file-1', fileName: 'a.md' },
        'color:1': { fileId: 'file-2', fileName: 'my-edition.md' },
      }),
    );
  });

  it('rejects invalid keys and invalid existing JSON', () => {
    assert.throws(() => mergeLibraryAdoptionEntry('', 'a/b', 'file-1', 'a.md'));
    assert.throws(() => mergeLibraryAdoptionEntry('not-json', '1', 'file-1', 'a.md'));
  });
});
