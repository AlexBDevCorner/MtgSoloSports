import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { ApiError, apiErrorMessage } from './api/http.ts';

describe('MSS-144 500 error rendering', () => {
  it('renders the server-provided corruption message for a 500 response', () => {
    const error = new ApiError(500, JSON.stringify({ error: 'Save is missing its RNG state.' }));
    assert.equal(apiErrorMessage(error), 'Save is missing its RNG state.');
  });

  it('keeps showing a short raw 500 detail so recoverable-error screens stay diagnostic', () => {
    assert.equal(apiErrorMessage(new ApiError(500, 'season status exploded')), 'season status exploded');
  });

  it('renders an actionable message when a 500 has no usable body instead of a bare status line', () => {
    const message = apiErrorMessage(new ApiError(500, ''));
    assert.ok(message.length > 0, 'a 500 must never render an empty message');
    assert.ok(message.includes('server logs'), 'a body-less 500 tells the user where to look');
    assert.ok(!message.startsWith('Request failed'), 'a 500 must not collapse into a bare status code');
  });

  it('renders the actionable message for a 5xx with an unusable long body', () => {
    const error = new ApiError(502, `<html><body>${'Bad Gateway '.repeat(40)}</body></html>`);
    assert.ok(apiErrorMessage(error).includes('server failed'));
  });

  it('keeps the bare status fallback for non-500 client errors without a body', () => {
    assert.equal(apiErrorMessage(new ApiError(409, '')), 'Request failed (409).');
  });

  it('keeps non-ApiError message passthrough', () => {
    assert.equal(apiErrorMessage(new Error('boom')), 'boom');
    assert.equal(apiErrorMessage('nope'), 'Something went wrong.');
  });
});
