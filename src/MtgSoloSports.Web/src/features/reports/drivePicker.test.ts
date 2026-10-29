import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  pickerConfigFromStatus,
  pickerDisabledHint,
  toPickerSelection,
} from './drivePicker';
import type { DriveStatus } from './drivePublishingApi';

function statusWith(overrides: Partial<DriveStatus>): DriveStatus {
  return {
    saveId: 'save-1',
    saveName: 'Save',
    currentSeason: 1,
    phase: 'Phase',
    configured: true,
    connected: true,
    scopes: 'https://www.googleapis.com/auth/drive.file',
    connectedUtc: null,
    dossier: {
      fileId: null,
      fileName: null,
      fingerprint: null,
      remoteModifiedTime: null,
      lastOutcome: null,
      webViewLink: null,
    },
    almanac: {
      fileId: null,
      fileName: null,
      fingerprint: null,
      remoteModifiedTime: null,
      lastOutcome: null,
      webViewLink: null,
    },
    mtshistoryFolderId: null,
    mtshistoryFolderName: null,
    lastPublishedUtc: null,
    lastSeasonNumber: null,
    lastPhase: null,
    lastGlobalStage: null,
    lastError: null,
    pickerClientId: 'client-123',
    pickerApiKey: 'picker-key',
    pickerEnabled: true,
    ...overrides,
  };
}

describe('MSS-089 Drive Picker grant path', () => {
  it('extracts a usable picker config only when enabled with both keys', () => {
    const config = pickerConfigFromStatus(statusWith({}));
    assert.deepEqual(config, { clientId: 'client-123', apiKey: 'picker-key' });
  });

  it('returns null when picker is disabled or keys are missing', () => {
    assert.equal(pickerConfigFromStatus(statusWith({ pickerEnabled: false })), null);
    assert.equal(pickerConfigFromStatus(statusWith({ pickerClientId: null })), null);
    assert.equal(pickerConfigFromStatus(statusWith({ pickerApiKey: '  ' })), null);
    assert.equal(pickerConfigFromStatus(null), null);
  });

  it('explains why the picker is unavailable without inventing access', () => {
    assert.match(pickerDisabledHint(null), /loading/i);
    assert.match(pickerDisabledHint(statusWith({ configured: false })), /not configured/i);
    assert.match(pickerDisabledHint(statusWith({ connected: false })), /Connect/i);
    assert.match(
      pickerDisabledHint(statusWith({ pickerEnabled: false, pickerApiKey: null })),
      /Browser API key/i,
    );
    assert.equal(pickerDisabledHint(statusWith({})), '');
  });

  it('normalizes picker docs and rejects id-less payloads', () => {
    assert.deepEqual(toPickerSelection({ id: 'file-1', name: 'a.md', mimeType: 'text/markdown' }), {
      id: 'file-1',
      name: 'a.md',
      mimeType: 'text/markdown',
    });
    assert.equal(toPickerSelection({ name: 'a.md' }), null);
    assert.equal(toPickerSelection({ id: '  ' }), null);
  });
});
