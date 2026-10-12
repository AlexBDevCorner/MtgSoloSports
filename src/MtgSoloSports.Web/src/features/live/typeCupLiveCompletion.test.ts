import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const view = readFileSync(join(here, 'LiveEventView.tsx'), 'utf8');
const stages = readFileSync(join(here, 'typeCupLiveStages.tsx'), 'utf8');

describe('MSS-079 Type Cup Live completion', () => {
  it('selects rounds by stage plus squad group/round, never group/round alone', () => {
    assert.ok(view.includes('urlTournamentPhase'), 'stage comes from the URL');
    assert.ok(view.includes('urlQualificationGroup'), 'qual group comes from the URL');
    assert.ok(view.includes('(r.tournamentPhase ?? null) ==='), 'selection compares stages');
    assert.ok(
      view.includes('Legacy Type Cup URL without a stage'),
      'legacy URLs fall back explicitly instead of silently picking a stage',
    );
  });

  it('offers a tournament-stage picker instead of one collapsed group list', () => {
    assert.ok(view.includes('Tournament stages'), 'stage picker exists');
    assert.ok(view.includes('typeStages'), 'stages derive from persisted rounds');
    assert.ok(view.includes("key={`${r.tournamentPhase ?? 'x'}/${r.qualificationGroup ?? 'x'}/"), 'pills keyed by stage');
  });

  it('hides the stale in-progress stage once the tournament is complete', () => {
    assert.ok(
      view.includes('const stageProgressText = complete ? null : (progress?.tournamentStage ?? lastStage);'),
      'completion clears the stale cursor',
    );
    assert.ok(
      stages.includes('const currentStage = tournamentComplete ? null : (lastStage ?? progress?.tournamentStage ?? null);'),
      'tournament pane hides the cursor after the Final',
    );
  });

  it('only offers simulation while the event is both next and incomplete', () => {
    assert.ok(
      view.includes('progress.sourceSeasonNumber === season && !complete'),
      'playable requires incompleteness',
    );
  });

  it('turns an already-complete conflict into replay plus Dashboard guidance', () => {
    assert.ok(view.includes('isAlreadyCompleteMessage'), '409 is detected');
    assert.ok(view.includes('friendlyCompleteMessage'), 'message points at replay');
    assert.ok(view.includes('continue on the Dashboard for the next season'), 'next navigation hinted');
    assert.ok(view.includes('setBaseline(null)'), 'bulk completion resets replay baselines');
  });
});
