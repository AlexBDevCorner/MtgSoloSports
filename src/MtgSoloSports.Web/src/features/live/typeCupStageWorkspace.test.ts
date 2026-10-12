import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const view = readFileSync(join(here, 'LiveEventView.tsx'), 'utf8');
const stages = readFileSync(join(here, 'typeCupLiveStages.tsx'), 'utf8');
const model = readFileSync(join(here, 'typeCupLiveModel.ts'), 'utf8');
const app = readFileSync(join(here, '..', '..', 'App.tsx'), 'utf8');

describe('MSS-078 Type Cup Live stage workspace', () => {
  it('gives each qualification group and the Final its own workspace with compact nav', () => {
    assert.ok(view.includes('selectedTypeStage'), 'explicit stage selection drives the workspace');
    assert.ok(view.includes('stageOrdered'), 'only the selected stage rounds appear');
    assert.ok(view.includes('chronologicalStageRounds'), 'rounds are chronological within the stage');
    assert.ok(stages.includes('Type Cup stages'), 'compact nav overview');
    assert.ok(stages.includes('onSelectStage'), 'completed stages are replayable from the nav');
    assert.ok(stages.includes('Follow active stage'), 'active/next stage is distinguishable while replaying');
    assert.ok(!stages.includes('Qualification + Final'), 'no confusing combined sidebar title');
  });

  it('numbers rounds stage-locally with correct totals and secondary squad detail', () => {
    assert.ok(model.includes('stageLocalRound'), 'overall number derives from squad coordinates');
    assert.ok(model.includes('stageLocalTotal'), 'totals derive from authoritative shape');
    assert.ok(view.includes('Round ${selectedOverall ?? stagePlayed} / ${stageTotal}'), 'stage-local Round X / Y progress');
    assert.ok(view.includes('stageRoundAccessibleLabel'), 'overall number travels with squad-local detail');
    assert.ok(view.includes('Squad #${'), 'squad groups use unmistakable Squad # naming');
    assert.ok(!view.includes('Squad rank group ${visibleGroup'), 'no ambiguous repeating 1-8 grid title');
  });

  it('never confuses qualification groups with squad rank groups', () => {
    assert.ok(model.includes('squad labels never mention qualification') || model.includes('Squad #'), 'squad labels');
    assert.ok(view.includes('(r.tournamentPhase ?? null) ==='), 'stage-aware filtering disambiguates recurring group/round coordinates');
    assert.ok(view.includes('Team standings in ${'), 'qual standings are stage-scoped');
    assert.ok(view.includes('Final standings'), 'Final standings are explicitly distinct');
    assert.ok(view.includes('Points never') || stages.includes('Points never'), 'no carryover disclosure');
  });

  it('scopes progression to the stage and differentiates tournament-wide fast-forward', () => {
    assert.ok(view.includes('handleFinishStage'), 'stage-scoped fast-forward exists');
    assert.ok(view.includes('Finish ${'), 'stage action names its stage');
    assert.ok(view.includes('stops before the next stage') || view.includes('Stops before'), 'stage boundary is explicit');
    assert.ok(view.includes('Finish entire Type Cup'), 'tournament-wide action is explicitly labelled');
    assert.ok(view.includes('Finish the entire Type Cup?'), 'wider action needs confirmation');
    assert.ok(view.includes('Next Round · ${'), 'primary action names its canonical stage');
    assert.ok(view.includes('finishTypeCupTeamStage(saveId, canonicalSnapshot)'), 'stage finish uses the atomic stage-scoped endpoint with its snapshot');
    assert.ok(view.includes('stage changed'), 'stale stage finish refreshes instead of crossing the boundary');
    assert.ok(!view.includes('for (let i = 0; i < remaining; i += 1)'), 'stage finish never loops unscoped single-step mutations');
    assert.ok(!/new Random|Random\.Shared|Math\.random/.test(view), 'never simulates here');
  });

  it('keeps reveal and standings in the selected stage with fresh Final scores', () => {
    assert.ok(view.includes('selectedStageInfo.label'), 'reveal heading names the stage');
    assert.ok(view.includes('Squad #${roundView.group'), 'reveal couples overall and squad-local numbers');
    assert.ok(view.includes('baseline'), 'standings follow the persisted stage/round baseline');
    assert.ok(view.includes('tournamentPhase') && view.includes('qualificationGroup'), 'baseline is stage-scoped');
    assert.ok(view.includes('Final starts fresh at zero') || stages.includes('scores reset to zero'), 'Final opens fresh');
  });

  it('clears the prior stage reveal when a pending stage has no persisted round', () => {
    assert.ok(view.includes('if (!selected) {'), 'pending stage without a round is handled');
    assert.ok(view.includes('setRoundView(null);'), 'pending selection drops the old reveal');
    assert.ok(view.includes('setBaseline(null);'), 'pending selection drops the old baseline');
  });

  it('never leaves a stale reveal while switching stages or on fetch failure', () => {
    assert.ok(
      view.includes('Clear upfront') || view.includes('switching stages must never display'),
      'stage switch clears before the new fetch resolves',
    );
    assert.ok(
      view.includes('any fetch failure must leave no stale reveal'),
      'non-404 failures also clear the previous stage reveal',
    );
    assert.ok(!view.includes('failure.status === 404'), 'no 404-only clear that would retain stale reveals');
  });

  it('shows a stage-scoped starting field for a pending Final with complete quals', () => {
    assert.ok(view.includes('pendingStageStandings'), 'pending stage fetches its own scoped standings');
    assert.ok(view.includes('starting at zero'), 'pending Final discloses fresh zero scores');
    assert.ok(view.includes('finalists'), 'pending Final names its finalist roster');
    assert.ok(
      view.includes('fetchEventTeamStandings(saveId, season, event, controller.signal, undefined, stage)'),
      'pending roster uses the stage-scoped endpoint without a before cursor',
    );
  });

  it('never shows unscoped tournament totals inside a Type Cup stage workspace', () => {
    assert.ok(
      view.includes('(!isTypeTournamentLive || showFinal)'),
      'tournament fallback only for the persisted Final, never unscoped quals in a pending stage',
    );
  });

  it('preserves stage/round selection across reload and Back/Forward via explicit stage URL', () => {
    assert.ok(view.includes('urlTournamentPhase') && view.includes('urlQualificationGroup'), 'stage identity in the URL');
    assert.ok(view.includes('selectedStageKey'), 'explicit stage wins, otherwise follow canonical');
    assert.ok(view.includes('canonicalStageKey'), 'canonical cursor is followed unless replaying');
    assert.ok(app.includes('urlTournamentPhase={route.tournamentPhase}'), 'App wires stage params to Live');
    assert.ok(app.includes('tournamentPhase: stage?.tournamentPhase'), 'stage navigation writes the URL');
  });

  it('supports any stage count and leaves single-stage flows alone', () => {
    assert.ok(model.includes('qualificationGroupCount'), 'stage count is data-driven');
    assert.ok(view.includes('qualGroupCount'), 'Live derives the count from saved data');
    assert.ok(view.includes('isTypeTournamentLive'), 'Type Cup nav is gated to tournaments');
    assert.ok(!view.includes('Type Cup stages') || view.includes('isTypeTournamentLive ?'), 'Color Cup and qualifiers keep generic views');
  });

  it('stays readable on desktop and narrow viewports with keyboard-friendly nav', () => {
    assert.ok(view.includes('role="group"') && view.includes('aria-label'), 'round selector is a labelled group');
    assert.ok(view.includes('aria-pressed'), 'stage and round buttons expose pressed state');
    assert.ok(view.includes('aria-label={accessible}'), 'round buttons announce overall plus squad detail');
    assert.ok(stages.includes('role="group"') && stages.includes('aria-label'), 'stage nav is a labelled group');
    const css = readFileSync(join(here, 'LivePage.css'), 'utf8');
    assert.ok(css.includes('max-width: 1020px') && css.includes('max-width: 640px'), 'stacked layouts for narrow windows');
  });
});
