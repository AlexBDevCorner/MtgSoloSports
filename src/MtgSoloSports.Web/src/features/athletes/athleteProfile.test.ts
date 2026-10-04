import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const read = (file: string) => readFileSync(join(here, file), 'utf8');

const hook = read('useAthleteProfile.ts');
const api = read('athleteApi.ts');
const page = read('AthleteProfilePage.tsx');

describe('athlete profile performance architecture', () => {
  it('never requests the global records endpoint from the athlete page', () => {
    assert.ok(!hook.includes('fetchRecords'), 'hook must not import fetchRecords');
    assert.ok(!hook.includes('records/recordsApi'), 'hook must not import the global records api');
    assert.ok(!hook.includes('GetRecordsHandler'), 'hook must not invoke GetRecordsHandler');
    assert.ok(!hook.includes('ScoreRecordLoader'), 'hook must not invoke ScoreRecordLoader');
    assert.ok(!api.includes('/api/saves/${saveId}/records'), 'athlete api must not expose global records');
  });

  it('uses an athlete-specific record-holdings read instead of whole-save scoring', () => {
    assert.ok(hook.includes('fetchAthleteRecordHoldings'), 'hook loads athlete holdings');
    assert.ok(api.includes('fetchAthleteRecordHoldings'), 'athlete api exposes holdings');
    assert.ok(api.includes('record-holdings'), 'holdings endpoint path present');
    assert.ok(api.includes('AthleteRecordHolding'), 'narrow holdings model present');
  });

  it('renders the core profile without waiting for secondary sections', () => {
    const profileEffect = hook.indexOf('Core profile');
    assert.ok(profileEffect >= 0, 'core profile section documented');
    // Core loading resolves in its own effect; secondary states never gate it.
    assert.ok(hook.includes('storiesLoading'), 'stories have independent loading state');
    assert.ok(hook.includes('holdingsLoading'), 'holdings have independent loading state');
    assert.ok(
      hook.includes('setLoading(false)') && hook.includes('fetchAthleteProfile'),
      'core loading resolves from the profile request alone',
    );
    assert.ok(
      !hook.includes('Promise.all') || hook.includes('independent'),
      'no single Promise.all gates the whole page',
    );
  });

  it('loads secondary sections concurrently once identity is known', () => {
    // Three independent effects keyed only on identity, not chained waterfalls.
    const effects = hook.match(/useEffect\(/g) ?? [];
    assert.ok(effects.length >= 3, `expected 3 independent effects, saw ${effects.length}`);
    assert.ok(!hook.includes('await fetchAthleteProfile(saveId, athleteId, signal);\n      let feed'), 'no profile -> stories waterfall');
    assert.ok(hook.includes('fetchAthleteStories(saveId, athleteId'), 'stories start from identity');
    assert.ok(hook.includes('fetchAthleteRecordHoldings(saveId, athleteId'), 'holdings start from identity');
  });

  it('keeps secondary failures from discarding a loaded profile', () => {
    assert.ok(hook.includes('storiesError'), 'stories have independent error state');
    assert.ok(hook.includes('holdingsError'), 'holdings have independent error state');
    assert.ok(
      hook.includes('setStoriesError(apiErrorMessage(failure))'),
      'stories failure degrades to section error',
    );
    assert.ok(
      hook.includes('setHoldingsError(apiErrorMessage(failure))'),
      'holdings failure degrades to section error',
    );
    assert.ok(page.includes('Stories unavailable'), 'stories error degrades gracefully');
    assert.ok(page.includes('Record holdings unavailable'), 'holdings error degrades gracefully');
  });

  it('preserves abort behaviour when navigating between athletes', () => {
    const aborts = hook.match(/controller\.abort\(\)/g) ?? [];
    assert.ok(aborts.length >= 3, `expected per-section abort, saw ${aborts.length}`);
  });

  it('gives secondary sections deliberate loading behaviour without page-wide spinners', () => {
    assert.ok(page.includes('Loading record holdings'), 'holdings show inline loading');
    assert.ok(page.includes('Loading stories'), 'stories show inline loading');
    assert.ok(
      page.includes('if (state.loading && !state.profile)'),
      'page-wide loading only gates the core profile',
    );
  });
});
