import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const view = readFileSync(join(here, 'LiveEventView.tsx'), 'utf8');
const app = readFileSync(join(here, '..', '..', 'App.tsx'), 'utf8');

describe('live event mode', () => {
  it('reuses the live layout and the manual live reveal', () => {
    for (const token of ['live-layout', 'live-sidebar', 'live-main', 'layout="live"', 'autoPlayOnStart={false}', 'round-pills']) {
      assert.ok(view.includes(token), token);
    }
  });

  it('plays one round per click and can finish the event', () => {
    assert.ok(view.includes('playEventRound(saveId, event)'), 'Next Round calls the step endpoint');
    assert.ok(view.includes('Next Round'));
    assert.ok(view.includes('runEventRemaining(saveId, event)'), 'Run remaining calls the one-shot endpoint');
    assert.ok(view.includes('Run remaining rounds'));
    assert.ok(view.includes('if (busy)'), 'duplicate submissions are blocked');
    assert.ok(view.includes('onMutated()'), 'dashboard status refreshes after each action');
  });

  it("offers play controls only for the save's next event and season", () => {
    assert.ok(view.includes('const playable = progress !== null && progress.sourceSeasonNumber === season;'));
    assert.ok(view.includes('complete ? ('), 'completion notice first');
    assert.ok(view.includes(') : playable ? ('), 'Next Round and Run remaining only when playable');
    assert.ok(view.includes('Not the next event'), 'otherwise points back to the Dashboard');
  });

  it('shows team standings and a completion notice', () => {
    assert.ok(view.includes('fetchEventTeamStandings'));
    assert.ok(view.includes('onRevealedChange={setRevealed}'), 'team standings follow the reveal step');
    assert.ok(view.includes('projectRevealedTeamStandings('), 'totals before the round plus revealed points');
    assert.ok(view.includes('{ group: shownGroup, round: shownRound }'), 'baseline excludes the shown round');
    assert.ok(view.includes('Continue on the Dashboard'));
    assert.ok(view.includes('View results'));
  });

  it('continues the Superleague qualifier to the next canonical qualifier on Live', () => {
    assert.ok(view.includes("nextQualifierLiveParam('superleague')"), 'next canonical qualifier after Superleague');
    assert.ok(view.includes('Play next qualifier on Live'), 'no Dashboard trip between qualifiers');
    assert.ok(view.includes('All 17 qualifiers'), 'overview link from the Superleague event');
    assert.ok(view.includes('runRemainingQualifiers(saveId)'), 'Live fast-forward for remaining qualifiers');
    assert.ok(view.includes('Run all remaining qualifiers'));
    assert.ok(view.includes('Qualifier 1 of 17 in canonical order'), 'phase position labelled as event count');
  });

  it('stays on the event after it completes because the URL keeps the event', () => {
    assert.ok(app.includes('<LiveEventView'));
    assert.ok(app.includes('route.event ?? dashboard.data?.status?.eventProgress?.event'));
  });
});

describe('type cup tournament live progression', () => {
  it('tracks the backend tournament stage instead of simulating', () => {
    assert.ok(view.includes('optionalTournament(saveId, season'), 'persisted tournament tables');
    assert.ok(view.includes('optionalDraw(saveId, season'), 'persisted draw for pending groups');
    assert.ok(view.includes('tournamentStage'), 'stage from the backend cursor');
    assert.ok(view.includes('runEventRemaining(saveId, event)'), 'fast path uses backend progression');
    assert.ok(!/new Random|Random\.Shared|Math\.random/.test(view), 'never simulates here');
  });

  it('shows qualification groups as active, completed or pending with text badges', () => {
    assert.ok(view.includes('<TypeCupLiveStages'), 'tournament progression card');
    const stages = readFileSync(join(here, 'typeCupLiveStages.tsx'), 'utf8');
    assert.ok(stages.includes('Qualification Group'), 'generic group identity for 3+ groups');
    assert.ok(stages.includes('Semifinal A'), 'friendly alias only for two groups');
    assert.ok(stages.includes('Completed'), 'completed text');
    assert.ok(stages.includes('Active'), 'active text');
    assert.ok(stages.includes('Pending'), 'pending text');
    assert.ok(stages.includes('badge'), 'status not carried by color alone');
  });

  it('keeps rank groups distinct from qualification groups and continues without dashboard trips', () => {
    assert.ok(view.includes('Squad rank group'), 'rank-group versus qual-group wording');
    assert.ok(view.includes('no Dashboard round trip'), 'continuation copy');
    assert.ok(view.includes('canonical draw order'), 'backend canonical order');
  });
});
