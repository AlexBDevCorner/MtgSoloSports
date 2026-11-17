import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const app = readFileSync(join(here, '..', '..', 'App.tsx'), 'utf8');
const shell = readFileSync(join(here, '..', 'shell', 'AppShell.tsx'), 'utf8');
const resolver = readFileSync(join(here, 'activeLiveEvent.ts'), 'utf8');
const cta = readFileSync(join(here, 'LiveDashboardCta.tsx'), 'utf8');

describe('MSS-143 Live follows the active season event', () => {
  it('resolves bare Live through the shared active-event resolver, not stale league defaults', () => {
    assert.ok(app.includes('resolveActiveLiveTarget'), 'bare Live uses the shared resolver');
    assert.ok(app.includes('isBareLive'), 'only the bare entry resolves; explicit deep links keep replay semantics');
    assert.ok(resolver.includes("next.liveEvent === 'chaos-cup'"), 'Chaos resolves via the legal next action, not eventProgress alone');
    assert.ok(resolver.includes('seasonFlow(progress, status)'), 'Live and Dashboard agree through seasonFlow');
  });

  it('presents the in-progress Chaos Cup on bare Live with its current round/stage view', () => {
    assert.ok(app.includes('target.kind === \'chaos\''), 'bare Live branches to Chaos when active');
    assert.ok(app.includes('<ChaosCupLiveView'), 'reuses the existing Chaos Cup presentation');
    assert.ok(app.includes('chaos-cup:active'), 'active Chaos remounts per season so round completion never shows stale data');
  });

  it('moves off stale competition after Chaos completion via the Dashboard CTA', () => {
    assert.ok(app.includes("target.kind === 'dashboard'"), 'post-Chaos transitions resolve to Dashboard, not league');
    assert.ok(app.includes('<LiveDashboardCta'), 'bare Live renders an explicit Dashboard CTA');
    assert.ok(cta.includes('Continue on the Dashboard'), 'CTA points at the Dashboard next step');
    assert.ok(cta.includes('Live •'), 'CTA labels which competition and phase Live is showing');
    assert.ok(!cta.includes('Current standings'), 'CTA never conflates the wait with past standings results');
    assert.ok(!cta.includes('data-table'), 'CTA never renders stale league tables as the active event');
  });

  it('keeps league play and supported non-Chaos postseason events on their Live views', () => {
    assert.ok(app.includes('<LivePage'), 'league play keeps the existing league/stage experience');
    assert.ok(app.includes("target.kind === 'event'"), 'other playable postseason phases resolve to their event view');
    assert.ok(app.includes('<LiveEventView'), 'reuses the existing event presentation');
    assert.ok(app.includes("target.kind === 'selection'"), 'Cup selections reuse the selection view on Live');
  });

  it('routes tiered feeder qualifiers to the per-qualifier Live view, not stale Superleague', () => {
    assert.ok(resolver.includes('resolveActiveQualifierParam'), 'resolver derives the active tiered qualifier from phase-wide progress');
    assert.ok(resolver.includes('canonicalQualifierLiveParams'), 'active qualifier follows canonical Superleague-first order');
    assert.ok(resolver.includes("kind: 'qualifier'"), 'feeder actives resolve to a qualifier target');
    assert.ok(app.includes("target.kind === 'qualifier'"), 'bare Live branches to the feeder qualifier when active');
    assert.ok(app.includes('<LiveQualifierView'), 'reuses the existing per-qualifier Live view');
    assert.ok(app.includes('qualifier:active'), 'active feeder remounts per qualifier/season so completion never shows stale data');
    assert.ok(app.includes('qualifierLivePath'), 'sidebar and bare Live link the active qualifier URL');
    assert.ok(resolver.includes('qualifierLivePath'), 'sidebar href names the active feeder qualifier');
  });

  it('points the sidebar Live entry at the resolved active competition', () => {
    assert.ok(app.includes('liveHref'), 'App resolves the sidebar destination from lifecycle state');
    assert.ok(app.includes('activeLivePath'), 'sidebar href reuses the shared resolver path');
    assert.ok(shell.includes('liveHref'), 'shell renders the resolved Live destination');
    assert.ok(shell.includes('Open the current live event'), 'Live still announces itself as the current event');
  });
});
