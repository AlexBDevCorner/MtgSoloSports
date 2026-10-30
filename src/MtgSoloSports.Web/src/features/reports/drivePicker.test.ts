import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  configurePickerBuilder,
  pickerConfigFromStatus,
  pickerDisabledHint,
  toPickerSelection,
  type PickerBuilderLike,
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
    seasonsFolderId: null,
    lastPublishedUtc: null,
    lastSeasonNumber: null,
    lastPhase: null,
    lastGlobalStage: null,
    lastError: null,
    pickerClientId: 'client-123',
    pickerApiKey: 'picker-key',
    pickerAppId: '1234567890',
    pickerEnabled: true,
    autoPublishEnabled: false,
    autoPublishStatus: 'Disabled',
    autoRequestedSeason: null,
    autoRequestedGlobalStage: null,
    autoRequestedPhase: null,
    autoRequestedUtc: null,
    autoAttemptCount: 0,
    autoNextRetryUtc: null,
    autoLastError: null,
    autoPausedForAuth: false,
    ...overrides,
  };
}

describe('MSS-089 Drive Picker grant path', () => {
  it('extracts a usable picker config only when enabled with all browser keys', () => {
    const config = pickerConfigFromStatus(statusWith({}));
    assert.deepEqual(config, { clientId: 'client-123', apiKey: 'picker-key', appId: '1234567890' });
  });

  it('returns null when picker is disabled or keys are missing', () => {
    assert.equal(pickerConfigFromStatus(statusWith({ pickerEnabled: false })), null);
    assert.equal(pickerConfigFromStatus(statusWith({ pickerClientId: null })), null);
    assert.equal(pickerConfigFromStatus(statusWith({ pickerApiKey: '  ' })), null);
    assert.equal(pickerConfigFromStatus(statusWith({ pickerAppId: null })), null);
    assert.equal(pickerConfigFromStatus(statusWith({ pickerAppId: '  ' })), null);
    assert.equal(pickerConfigFromStatus(null), null);
  });

  it('configures the Picker builder with the Cloud project number via setAppId', () => {
    const calls: string[] = [];
    let built = false;
    const fakeBuilder = {
      addView: () => {
        calls.push('addView');
        return undefined;
      },
      setOAuthToken: (token: string) => {
        calls.push(`setOAuthToken:${token}`);
        return undefined;
      },
      setDeveloperKey: (key: string) => {
        calls.push(`setDeveloperKey:${key}`);
        return undefined;
      },
      setAppId: (appId: string) => {
        calls.push(`setAppId:${appId}`);
        return undefined;
      },
      setCallback: () => {
        calls.push('setCallback');
        return undefined;
      },
      build: () => {
        built = true;
        return { setVisible: () => undefined };
      },
    } satisfies PickerBuilderLike;
    const picker = configurePickerBuilder(
      fakeBuilder,
      { kind: 'docs' },
      { clientId: 'client-123', apiKey: 'picker-key', appId: '1234567890' },
      'token-1',
      () => undefined,
    );
    assert.equal(built, true);
    assert.deepEqual(calls, [
      'addView',
      'setOAuthToken:token-1',
      'setDeveloperKey:picker-key',
      'setAppId:1234567890',
      'setCallback',
    ]);
    assert.equal(typeof picker.setVisible, 'function');
  });

  it('explains why the picker is unavailable without inventing access', () => {
    assert.match(pickerDisabledHint(null), /loading/i);
    assert.match(pickerDisabledHint(statusWith({ configured: false })), /not configured/i);
    assert.match(pickerDisabledHint(statusWith({ connected: false })), /Connect/i);
    assert.match(
      pickerDisabledHint(statusWith({ pickerEnabled: false, pickerApiKey: null })),
      /Browser API key/i,
    );
    assert.match(
      pickerDisabledHint(statusWith({ pickerEnabled: false, pickerAppId: null })),
      /project number/i,
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
