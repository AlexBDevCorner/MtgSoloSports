import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  driveAuthUrl,
  driveDestinationUrl,
  driveDisconnectUrl,
  drivePublishUrl,
  driveStatusUrl,
  driveVerifyUrl,
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
