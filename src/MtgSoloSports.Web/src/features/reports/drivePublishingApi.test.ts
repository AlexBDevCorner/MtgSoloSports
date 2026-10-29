import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  describeAutoPublishStatus,
  driveAutoPublishRetryUrl,
  driveAutoPublishUrl,
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
