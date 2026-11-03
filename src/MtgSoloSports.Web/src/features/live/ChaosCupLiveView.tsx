import { Fragment, useEffect, useState } from 'react';
import { ApiError, apiErrorMessage } from '../../shared/api/http';
import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import {
  completeChaosCup,
  completeChaosStage,
  drawChaosStage,
  fetchChaosEdition,
  fetchChaosGroup,
  fetchChaosRound,
  finishChaosGroup,
  playChaosRound,
  type ChaosCupEdition,
  type ChaosCupGroup,
  type ChaosCupRound,
} from '../cups/chaosCupApi';
import { fetchSeasonStatus, type SeasonStatus } from '../dashboard/dashboardApi';
import { dashboardPath } from '../routing/routes';
import { AthleteLink, Link } from '../routing/router';
import { RoundReveal } from '../reveal/RoundReveal';
import type { RevealPlacement } from '../reveal/types';
import {
  CHAOS_ADVANCING,
  CHAOS_ROUNDS_PER_GROUP,
  canonicalChaosStage,
  chaosCutLabel,
  chaosGroupCount,
  chaosStageLabel,
  chaosStageShortLabel,
  chaosStageStatus,
  chaosStageStatusLabel,
  formatChaosPoints,
  isChaosLivePlayable,
  isFinalChaosStage,
  nextDrawableChaosStage,
  selectedChaosGroup,
  selectedChaosRound,
  selectedChaosStage,
} from './chaosCupLiveModel';
import './LivePage.css';

/** True once the backend reports the tournament as already resolved (HTTP 409). */
function isAlreadyCompleteMessage(message: string): boolean {
  const normalized = message.toLowerCase();
  return normalized.includes('already been resolved') || normalized.includes('already complete');
}

/**
 * Chaos Cup group navigation with scoped Live controls (MSS-109/110). Reuses
 * the Live sidebar/main layout and the shared RoundReveal presentation; the
 * backend stays authoritative and the view only re-reads the affected
 * edition/group/round after each scoped POST — never the whole 1008-round
 * tournament. Each (stage, group) owns an isolated RNG, so group order and
 * incremental-vs-complete calls stay byte-identical.
 */
export function ChaosCupLiveView({
  saveId,
  season,
  urlStage,
  urlGroup,
  urlRound,
  onSelect,
  onMutated,
}: {
  saveId: string;
  /** Source season from `?season=`; null follows the latest Chaos edition. */
  season: number | null;
  urlStage: number | null;
  urlGroup: number | null;
  urlRound: number | null;
  /** Stable shareable selection; a null round follows the latest played round. */
  onSelect: (stage: number, group: number, round: number | null) => void;
  /** Refreshes the Dashboard status so both surfaces agree on the next event. */
  onMutated?: () => void;
}) {
  const [edition, setEdition] = useState<ChaosCupEdition | null>(null);
  const [editionLoading, setEditionLoading] = useState(true);
  const [editionError, setEditionError] = useState<string | null>(null);
  const [editionMissing, setEditionMissing] = useState(false);
  const [group, setGroup] = useState<ChaosCupGroup | null>(null);
  const [groupLoading, setGroupLoading] = useState(false);
  const [groupError, setGroupError] = useState<string | null>(null);
  const [groupMissing, setGroupMissing] = useState(false);
  const [roundView, setRoundView] = useState<ChaosCupRound | null>(null);
  const [roundLoading, setRoundLoading] = useState(false);
  const [roundError, setRoundError] = useState<string | null>(null);
  const [revision, setRevision] = useState(0);
  const [status, setStatus] = useState<SeasonStatus | null>(null);
  const [statusLoading, setStatusLoading] = useState(true);
  const [statusError, setStatusError] = useState<string | null>(null);
  const [pending, setPending] = useState<'round' | 'group' | 'stage' | 'draw' | 'cup' | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [confirmEntire, setConfirmEntire] = useState(false);

  useEffect(() => {
    const controller = new AbortController();
    setEditionLoading(true);
    setEditionError(null);
    setEditionMissing(false);
    fetchChaosEdition(saveId, season, controller.signal)
      .then((loaded) => {
        setEdition(loaded);
        setEditionLoading(false);
      })
      .catch((failure: unknown) => {
        if (failure instanceof DOMException && failure.name === 'AbortError') {
          return;
        }
        if (failure instanceof ApiError && failure.status === 404) {
          setEdition(null);
          setEditionMissing(true);
        } else {
          setEditionError(apiErrorMessage(failure));
        }
        setEditionLoading(false);
      });
    return () => controller.abort();
  }, [saveId, season, revision]);

  useEffect(() => {
    const controller = new AbortController();
    setStatusLoading(true);
    setStatusError(null);
    fetchSeasonStatus(saveId, controller.signal)
      .then((loaded) => {
        setStatus(loaded);
        setStatusLoading(false);
      })
      .catch((failure: unknown) => {
        if (failure instanceof DOMException && failure.name === 'AbortError') {
          return;
        }
        setStatus(null);
        setStatusError(apiErrorMessage(failure));
        setStatusLoading(false);
      });
    return () => controller.abort();
  }, [saveId, revision]);

  const stage = selectedChaosStage(urlStage, edition);
  const stageSummary = edition?.stages.find((entry) => entry.stageIndex === stage) ?? null;
  const stageDrawn = stageSummary?.isDrawn ?? false;
  const groupNumber = selectedChaosGroup(urlGroup, stage);
  // Once the edition resolves, pin group/round fetches to its season so a
  // latest-edition view never drifts between seasons mid-navigation.
  const resolvedSeason = season ?? edition?.sourceSeasonNumber ?? null;
  // Only trust the loaded group when it matches the URL selection; the fetch
  // effect clears stale groups, and this guard covers the render between
  // selection change and fetch completion.
  const groupMatchesSelection =
    group !== null && group.stageIndex === stage && group.groupNumber === groupNumber;
  const visibleGroup = groupMatchesSelection ? group : null;

  useEffect(() => {
    if (!edition || !stageDrawn || resolvedSeason === null) {
      setGroup(null);
      setGroupLoading(false);
      setGroupError(null);
      setGroupMissing(false);
      setRoundView(null);
      setRoundError(null);
      return;
    }
    // Pin this fetch to the requested selection so a slower stale response
    // can never overwrite a newer group (mirrors the round effect below).
    const expected = { stage, group: groupNumber };
    const controller = new AbortController();
    // Clear the previous group/round immediately so the URL, picker and panel
    // never show different groups while the new selection loads.
    setGroup(null);
    setRoundView(null);
    setRoundError(null);
    setGroupLoading(true);
    setGroupError(null);
    setGroupMissing(false);
    fetchChaosGroup(saveId, stage, groupNumber, resolvedSeason, controller.signal)
      .then((loaded) => {
        if (controller.signal.aborted) {
          return;
        }
        if (loaded.stageIndex !== expected.stage || loaded.groupNumber !== expected.group) {
          return;
        }
        setGroup(loaded);
        setGroupLoading(false);
      })
      .catch((failure: unknown) => {
        if (failure instanceof DOMException && failure.name === 'AbortError') {
          return;
        }
        if (controller.signal.aborted) {
          return;
        }
        // Never retain the previous group's standings under the new URL.
        setGroup(null);
        setRoundView(null);
        if (failure instanceof ApiError && failure.status === 404) {
          setGroupMissing(true);
        } else {
          setGroupError(apiErrorMessage(failure));
        }
        setGroupLoading(false);
      });
    return () => controller.abort();
  }, [saveId, stage, groupNumber, resolvedSeason, edition, stageDrawn, revision]);

  const wantedRound = selectedChaosRound(urlRound, visibleGroup?.roundsPlayed ?? null);
  useEffect(() => {
    if (!visibleGroup || wantedRound === null || resolvedSeason === null) {
      setRoundView(null);
      setRoundLoading(false);
      setRoundError(null);
      return;
    }
    // A stale slower response must never overwrite the freshly selected round.
    const expected = { stage: visibleGroup.stageIndex, group: visibleGroup.groupNumber, round: wantedRound };
    const controller = new AbortController();
    setRoundLoading(true);
    setRoundError(null);
    fetchChaosRound(saveId, visibleGroup.stageIndex, visibleGroup.groupNumber, wantedRound, resolvedSeason, controller.signal)
      .then((loaded) => {
        if (controller.signal.aborted) {
          return;
        }
        if (
          loaded.stageIndex !== expected.stage ||
          loaded.groupNumber !== expected.group ||
          loaded.roundNumber !== expected.round
        ) {
          return;
        }
        setRoundView(loaded);
        setRoundLoading(false);
      })
      .catch((failure: unknown) => {
        if (failure instanceof DOMException && failure.name === 'AbortError') {
          return;
        }
        if (controller.signal.aborted) {
          return;
        }
        setRoundView(null);
        setRoundError(apiErrorMessage(failure));
        setRoundLoading(false);
      });
    return () => controller.abort();
  }, [saveId, visibleGroup, wantedRound, resolvedSeason, revision]);

  function refresh(): void {
    setRevision((value) => value + 1);
  }

  // MSS-110 scoped controls: Dashboard and Live agree through the single
  // legal next action. Historical editions (or a post-Chaos phase) replay
  // read-only; only the active edition while `RunChaosCup` is legal may
  // simulate, and interrupted work resumes from persisted rounds.
  const playable = isChaosLivePlayable(
    status?.legalNextActions,
    status?.sourceSeasonNumber,
    edition?.sourceSeasonNumber ?? null,
  );
  const busy = pending !== null;
  const drawableStage = nextDrawableChaosStage(edition);
  const canPlayRound =
    playable && !busy && stageDrawn && !statusLoading && (visibleGroup ? !visibleGroup.isComplete : true);
  const canFinishGroup = playable && !busy && stageDrawn && !statusLoading && (visibleGroup ? !visibleGroup.isComplete : true);
  const canFinishStage =
    playable && !busy && stageDrawn && !statusLoading && !(stageSummary?.isComplete ?? false);
  const canDraw = playable && !busy && !statusLoading && drawableStage !== null;
  const canFinishCup = playable && !busy && !statusLoading && !(edition?.isComplete ?? true);

  function friendlyActionMessage(message: string): string {
    if (isAlreadyCompleteMessage(message)) {
      return `${message} The completed results stay replayable below; continue on the Dashboard for the next season.`;
    }
    return message;
  }

  function afterMutation(): void {
    setConfirmEntire(false);
    refresh();
    onMutated?.();
  }

  async function runScoped(kind: 'round' | 'group' | 'stage' | 'draw' | 'cup', call: () => Promise<unknown>): Promise<boolean> {
    if (busy) {
      return false;
    }
    setPending(kind);
    setActionError(null);
    try {
      await call();
      afterMutation();
      return true;
    } catch (failure) {
      setActionError(friendlyActionMessage(apiErrorMessage(failure)));
      return false;
    } finally {
      setPending(null);
    }
  }

  function handlePlayRound(): void {
    if (!canPlayRound || resolvedSeason === null) {
      return;
    }
    const pinnedStage = stage;
    const pinnedGroup = groupNumber;
    void runScoped('round', () => playChaosRound(saveId, pinnedStage, pinnedGroup, resolvedSeason)).then((ok) => {
      if (ok) {
        // Stay on the same group and follow the latest persisted round.
        onSelect(pinnedStage, pinnedGroup, null);
      }
    });
  }

  function handleFinishGroup(): void {
    if (!canFinishGroup || resolvedSeason === null) {
      return;
    }
    const pinnedStage = stage;
    const pinnedGroup = groupNumber;
    void runScoped('group', () => finishChaosGroup(saveId, pinnedStage, pinnedGroup, resolvedSeason)).then((ok) => {
      if (ok) {
        onSelect(pinnedStage, pinnedGroup, null);
      }
    });
  }

  function handleFinishStage(): void {
    if (!canFinishStage || resolvedSeason === null) {
      return;
    }
    const pinnedStage = stage;
    const pinnedGroup = groupNumber;
    void runScoped('stage', () => completeChaosStage(saveId, pinnedStage, resolvedSeason)).then((ok) => {
      if (ok) {
        onSelect(pinnedStage, pinnedGroup, null);
      }
    });
  }

  function handleDrawNext(): void {
    if (!canDraw || drawableStage === null || resolvedSeason === null) {
      return;
    }
    const target = drawableStage;
    void runScoped('draw', () => drawChaosStage(saveId, target, resolvedSeason)).then((ok) => {
      if (ok) {
        // The fresh stage starts at group 1 with no rounds yet.
        onSelect(target, 1, null);
      }
    });
  }

  function handleFinishCup(): void {
    if (!canFinishCup || resolvedSeason === null) {
      return;
    }
    if (!confirmEntire) {
      setConfirmEntire(true);
      return;
    }
    void runScoped('cup', () => completeChaosCup(saveId, resolvedSeason));
  }

  function handleSelectStage(nextStage: number): void {
    // Group counts differ per stage, so a stage switch restarts at group 1
    // and follows the latest played round there.
    onSelect(nextStage, 1, null);
  }

  function handleSelectGroup(nextGroup: number): void {
    onSelect(stage, nextGroup, null);
  }

  function handleSelectRound(nextRound: number): void {
    onSelect(stage, groupNumber, nextRound);
  }

  if (editionLoading && !edition) {
    return <Loading label="Loading Chaos Cup…" />;
  }
  if (editionMissing) {
    return (
      <Notice tone="empty" title="Chaos Cup unavailable">
        <p>
          {season !== null
            ? `No Chaos Cup field has been selected for Season ${season} in this save.`
            : 'No Chaos Cup has been selected in this save yet.'}{' '}
          Older seasons keep their existing history.
        </p>
        <p>
          <Link to={dashboardPath(saveId)} className="primary-button">
            Continue on the Dashboard
          </Link>
        </p>
      </Notice>
    );
  }
  if (!edition) {
    return (
      <Notice tone="error" title="Chaos Cup unavailable">
        <p>{editionError ?? 'Sporting progress could not be loaded for this save.'}</p>
        <p>
          <button type="button" className="ghost-button" onClick={refresh}>
            Retry
          </button>
        </p>
      </Notice>
    );
  }

  const canonical = canonicalChaosStage(edition);
  const viewingOther = canonical !== null && canonical !== stage;
  const groupCount = chaosGroupCount(stage);
  const groups = Array.from({ length: groupCount }, (_, index) => index + 1);
  const placements: RevealPlacement[] = (roundView?.placements ?? []).map((placement) => ({
    athleteId: placement.athleteId,
    name: placement.name,
    position: placement.position,
    baseThousandths: placement.baseThousandths,
    activeBonusThousandths: placement.activeBonusThousandths,
    finalThousandths: placement.finalThousandths,
    cumulativeBeforeThousandths: placement.cumulativeBeforeThousandths,
    cumulativeAfterThousandths: placement.cumulativeAfterThousandths,
    rankBefore: placement.rankBefore,
    rankAfter: placement.rankAfter,
    rankMovement: placement.rankMovement,
    imageUrl: placement.imageUrl,
    setCode: null,
    typeLine: '',
  }));
  const revealKey = roundView
    ? `${roundView.stageIndex}:${roundView.groupNumber}:${roundView.roundNumber}:${roundView.checksum}`
    : null;
  const revealMeta = roundView
    ? `Round ${roundView.roundNumber} · rules v${roundView.rulesVersion} · checksum ` +
      (roundView.checksum.length > 12 ? `${roundView.checksum.slice(0, 12)}…` : roundView.checksum)
    : undefined;

  return (
    <div className="live-layout">
      <aside className="live-sidebar" aria-label="Chaos Cup navigation">
        <Card eyebrow="Chaos Cup" title={`Chaos Cup · Season ${edition.sourceSeasonNumber}`}>
          <p className="muted small live-count">
            {edition.isComplete ? 'Tournament complete' : 'Tournament in progress'} · {edition.roundsPlayed}/
            {edition.totalRounds} rounds · {edition.groupsCompleted}/{edition.totalGroups} groups
          </p>
          {edition.isComplete ? (
            <Notice tone="info" title="Chaos Cup complete">
              <p>
                The Final holds official places 1–32. Replay any group below; nothing here resimulates.
              </p>
              <p>
                <Link to={dashboardPath(saveId)} className="ghost-button">
                  Continue on the Dashboard
                </Link>
              </p>
            </Notice>
          ) : null}
          {viewingOther && canonical !== null ? (
            <p className="muted small">
              Viewing {isFinalChaosStage(stage) ? 'Final' : `Stage ${stage + 1}`} (replay). Active:{' '}
              {isFinalChaosStage(canonical) ? 'Final' : `Stage ${canonical + 1}`}.{' '}
              <button type="button" className="ghost-button" onClick={() => handleSelectStage(canonical)}>
                Follow active stage
              </button>
            </p>
          ) : null}
          <details className="live-help">
            <summary>About Chaos replay</summary>
            <p className="muted small">
              The backend result is authoritative; refreshing only re-reads persisted rounds and never
              resimulates. Group scores reset every stage; only the selected group&apos;s rounds are
              fetched, never the whole tournament.
            </p>
          </details>
        </Card>

        <Card eyebrow="Live controls" title={playable ? 'Play the active stage' : 'Chaos Cup controls'}>
          {statusLoading ? (
            <Loading label="Loading next event…" />
          ) : statusError ? (
            <Notice tone="error" title="Next event unavailable">
              <p>{statusError}</p>
              <p>
                <button
                  type="button"
                  className="ghost-button"
                  onClick={refresh}
                >
                  Retry
                </button>
              </p>
            </Notice>
          ) : playable ? (
            <div className="live-manage-controls">
              <div className="live-buttons">
                <button
                  type="button"
                  className="primary-button"
                  disabled={!canPlayRound}
                  aria-busy={pending === 'round'}
                  title={
                    !stageDrawn
                      ? 'This stage has no persisted draw yet.'
                      : visibleGroup?.isComplete
                        ? 'This group already holds all 16 persisted rounds.'
                        : `Simulate the next round of Stage ${stage + 1} Group ${groupNumber} on the backend.`
                  }
                  onClick={handlePlayRound}
                >
                  {pending === 'round' ? 'Simulating…' : `Play next round · Group ${groupNumber}`}
                </button>
                <button
                  type="button"
                  className="ghost-button"
                  disabled={!canFinishGroup}
                  aria-busy={pending === 'group'}
                  title={
                    !stageDrawn
                      ? 'This stage has no persisted draw yet.'
                      : visibleGroup?.isComplete
                        ? 'This group already holds all 16 persisted rounds.'
                        : `Simulate the remaining rounds of Stage ${stage + 1} Group ${groupNumber} in canonical order.`
                  }
                  onClick={handleFinishGroup}
                >
                  {pending === 'group' ? 'Finishing…' : `Finish group ${groupNumber}`}
                </button>
                <button
                  type="button"
                  className="ghost-button"
                  disabled={!canFinishStage}
                  aria-busy={pending === 'stage'}
                  title={
                    !stageDrawn
                      ? 'This stage has no persisted draw yet.'
                      : (stageSummary?.isComplete ?? false)
                        ? 'This stage already holds every group result.'
                        : `Simulate every remaining group of ${isFinalChaosStage(stage) ? 'the Final' : `Stage ${stage + 1}`} in canonical group order; stops before the next draw.`
                  }
                  onClick={handleFinishStage}
                >
                  {pending === 'stage'
                    ? 'Finishing…'
                    : isFinalChaosStage(stage)
                      ? 'Finish Final'
                      : `Finish Stage ${stage + 1}`}
                </button>
                {drawableStage !== null ? (
                  <button
                    type="button"
                    className="ghost-button"
                    disabled={!canDraw}
                    aria-busy={pending === 'draw'}
                    title={`Draw ${isFinalChaosStage(drawableStage) ? 'the Final' : `Stage ${drawableStage + 1}`} now that ${isFinalChaosStage(stage) ? 'the previous stage' : `Stage ${stage + 1}`} is complete; pure-random groups of 32.`}
                    onClick={handleDrawNext}
                  >
                    {pending === 'draw'
                      ? 'Drawing…'
                      : isFinalChaosStage(drawableStage)
                        ? 'Draw Final'
                        : `Draw Stage ${drawableStage + 1}`}
                  </button>
                ) : null}
                <button
                  type="button"
                  className="ghost-button"
                  disabled={!canFinishCup}
                  aria-busy={pending === 'cup'}
                  title="Tournament-wide fast-forward: plays every remaining group and the Final."
                  onClick={handleFinishCup}
                >
                  {pending === 'cup' ? 'Finishing…' : 'Finish entire Cup'}
                </button>
              </div>
              <p className="muted small">Each round is saved and can&apos;t be undone.</p>
              <p className="muted small live-count">
                Group {groupNumber} · {visibleGroup?.roundsPlayed ?? 0}/{CHAOS_ROUNDS_PER_GROUP} rounds
                {visibleGroup?.isComplete ? ' · group complete' : ''} · Stage {stage + 1} ·{' '}
                {stageSummary?.groupsCompleted ?? 0}/{stageSummary?.groupCount ?? chaosGroupCount(stage)} groups
              </p>
              {confirmEntire ? (
                <Notice tone="error" title="Finish the entire Chaos Cup?">
                  <p>
                    This plays every remaining group and the Final to the official places. To stop at the
                    end of {isFinalChaosStage(stage) ? 'the Final' : `Stage ${stage + 1}`}, use{' '}
                    {isFinalChaosStage(stage) ? 'Finish Final' : `Finish Stage ${stage + 1}`} instead.
                  </p>
                  <p className="live-buttons">
                    <button
                      type="button"
                      className="ghost-button"
                      disabled={busy}
                      onClick={handleFinishCup}
                    >
                      {pending === 'cup' ? 'Finishing…' : 'Yes, finish entire Cup'}
                    </button>
                    <button
                      type="button"
                      className="ghost-button"
                      disabled={busy}
                      onClick={() => setConfirmEntire(false)}
                    >
                      Keep stage scope
                    </button>
                  </p>
                </Notice>
              ) : null}
              {actionError ? (
                <Notice tone="error" title="The round did not complete">
                  <p>{actionError}</p>
                  <p>
                    <button type="button" className="ghost-button" onClick={() => setActionError(null)}>
                      Dismiss
                    </button>
                  </p>
                </Notice>
              ) : null}
            </div>
          ) : (
            <Notice tone="info" title="Not the next event">
              <p>
                {edition.isComplete
                  ? 'This Chaos Cup is complete and replayable below.'
                  : status?.legalNextActions[0]
                    ? `The Dashboard names “${status.legalNextActions[0]}” next; scoped Chaos rounds unlock while “RunChaosCup” is legal for Season ${edition.sourceSeasonNumber}.`
                    : 'Scoped Chaos rounds unlock while “RunChaosCup” is the legal next event.'}{' '}
                Refreshing re-reads persisted rounds and never resimulates.
              </p>
              <p className="live-buttons">
                <Link to={dashboardPath(saveId)} className="primary-button">
                  Continue on the Dashboard
                </Link>
              </p>
            </Notice>
          )}
          {actionError && !playable ? (
            <Notice tone="error" title="The round did not complete">
              <p>{actionError}</p>
              <p>
                <button type="button" className="ghost-button" onClick={() => setActionError(null)}>
                  Dismiss
                </button>
              </p>
            </Notice>
          ) : null}
        </Card>

        <Card eyebrow="Stages" title={`Stages · ${edition.stageCount} of 6`}>
          <div className="segmented" role="group" aria-label="Chaos Cup stages">
            {edition.stages.map((entry) => {
              const status = chaosStageStatus(entry.state, entry.isDrawn, entry.isComplete);
              const active = entry.stageIndex === stage;
              const badgeClass =
                status === 'completed' ? 'badge badge-done' : status === 'active' ? 'badge badge-ready' : 'badge badge-wait';
              return (
                <button
                  key={entry.stageIndex}
                  type="button"
                  className={active ? 'nav-item current' : 'nav-item'}
                  aria-pressed={active}
                  aria-label={`${chaosStageLabel(entry.stageIndex)}, ${chaosStageStatusLabel(status)}, ${entry.roundsPlayed} of ${entry.totalRounds} rounds`}
                  title={
                    status === 'notDrawn'
                      ? `${chaosStageLabel(entry.stageIndex)} is not drawn yet`
                      : `${chaosStageLabel(entry.stageIndex)} · ${entry.roundsPlayed}/${entry.totalRounds} rounds · ${entry.groupsCompleted}/${entry.groupCount} groups`
                  }
                  onClick={() => handleSelectStage(entry.stageIndex)}
                >
                  {chaosStageShortLabel(entry.stageIndex)} · <span className={badgeClass}>{chaosStageStatusLabel(status)}</span>
                </button>
              );
            })}
          </div>
          {stageSummary ? (
            <p className="muted small">
              {chaosStageLabel(stage)} · {stageSummary.roundsPlayed}/{stageSummary.totalRounds} rounds ·{' '}
              {stageSummary.groupsCompleted}/{stageSummary.groupCount} groups complete
            </p>
          ) : null}
        </Card>

        <Card eyebrow="Groups" title={stageDrawn ? `${isFinalChaosStage(stage) ? 'Final' : `Stage ${stage + 1}`} · ${groupCount} group${groupCount === 1 ? '' : 's'}` : 'Groups'}>
          {!stageDrawn ? (
            <Notice tone="empty" title="Stage not drawn yet">
              <p>
                {chaosStageLabel(stage)} has no persisted draw in Season {edition.sourceSeasonNumber}; later
                stages unlock in canonical order once every group completes.
              </p>
            </Notice>
          ) : (
            <>
              <div className="round-pills round-pills-compact" role="group" aria-label={`Groups in ${chaosStageLabel(stage)}`}>
                {groups.map((number) => (
                  <button
                    key={number}
                    type="button"
                    className={number === groupNumber ? 'nav-item current' : 'nav-item'}
                    aria-pressed={number === groupNumber}
                    aria-label={`Group ${number}${number === groupNumber ? ', selected' : ''}`}
                    onClick={() => handleSelectGroup(number)}
                  >
                    {number}
                  </button>
                ))}
              </div>
              <p className="muted small">
                {groupLoading && !visibleGroup ? (
                  'Loading group…'
                ) : (
                  <>
                    Group {groupNumber} · {visibleGroup?.roundsPlayed ?? 0}/{CHAOS_ROUNDS_PER_GROUP} rounds
                    {visibleGroup?.isComplete ? ' · group complete' : ''}
                  </>
                )}
              </p>
            </>
          )}
        </Card>

        <Card eyebrow="Rounds" title={visibleGroup ? `Group ${visibleGroup.groupNumber} · ${visibleGroup.roundsPlayed}/${visibleGroup.totalRounds}` : 'Rounds'}>
          {!stageDrawn ? (
            <p className="muted small">Draw the stage before rounds can be replayed.</p>
          ) : groupError ? (
            <Notice tone="error" title="Group unavailable">
              <p>{groupError}</p>
              <p>
                <button type="button" className="ghost-button" onClick={refresh}>
                  Retry
                </button>
              </p>
            </Notice>
          ) : groupMissing ? (
            <Notice tone="empty" title="Group not drawn">
              <p>
                Stage {stage + 1} group {groupNumber} has no persisted draw yet for Season{' '}
                {edition.sourceSeasonNumber}.
              </p>
            </Notice>
          ) : groupLoading || !visibleGroup ? (
            <Loading label="Loading group…" />
          ) : visibleGroup.rounds.length === 0 ? (
            <p className="muted small">No rounds played in this group yet.</p>
          ) : (
            <div className="round-pills round-pills-compact" role="group" aria-label={`Played rounds in group ${visibleGroup.groupNumber}`}>
              {visibleGroup.rounds.map((round) => (
                <button
                  key={round.roundNumber}
                  type="button"
                  className={(wantedRound ?? visibleGroup.roundsPlayed) === round.roundNumber ? 'nav-item current' : 'nav-item'}
                  aria-pressed={(wantedRound ?? visibleGroup.roundsPlayed) === round.roundNumber}
                  onClick={() => handleSelectRound(round.roundNumber)}
                >
                  {round.roundNumber}
                </button>
              ))}
            </div>
          )}
        </Card>
      </aside>

      <div className="live-main">
        {!stageDrawn ? (
          <Notice tone="empty" title="Stage not drawn yet">
            <p>
              {chaosStageLabel(stage)} has no persisted draw in Season {edition.sourceSeasonNumber}. Pick a
              drawn stage above to replay its groups.
            </p>
          </Notice>
        ) : groupError ? (
          <Notice tone="error" title="Group unavailable">
            <p>{groupError}</p>
            <p>
              <button type="button" className="ghost-button" onClick={refresh}>
                Retry
              </button>
            </p>
          </Notice>
        ) : groupMissing ? (
          <Notice tone="empty" title="Group not drawn">
            <p>
              Stage {stage + 1} group {groupNumber} has no persisted draw yet for Season{' '}
              {edition.sourceSeasonNumber}.
            </p>
          </Notice>
        ) : groupLoading || !visibleGroup ? (
          <Loading label="Loading group…" />
        ) : (
          <>
            {wantedRound !== null && roundView && revealKey ? (
              <RoundReveal
                placements={placements}
                revealKey={revealKey}
                roundLabel={`Stage ${stage + 1} · Group ${visibleGroup.groupNumber} · Round ${roundView.roundNumber} results`}
                meta={revealMeta}
                autoPlayOnStart={false}
                layout="live"
                saveId={saveId}
              />
            ) : roundLoading ? (
              <Loading label="Loading round…" />
            ) : roundError ? (
              <Notice tone="error" title="Round unavailable">
                <p>{roundError}</p>
                <p>
                  <button type="button" className="ghost-button" onClick={refresh}>
                    Retry
                  </button>
                </p>
              </Notice>
            ) : (
              <Notice tone="empty" title="No rounds yet">
                <p>Group {visibleGroup.groupNumber} is drawn but no rounds have been played yet.</p>
              </Notice>
            )}

            <Card
              eyebrow={visibleGroup.standingsProvisional ? 'Provisional standings' : 'Final standings'}
              title={
                visibleGroup.isFinal
                  ? `Final · Group ${visibleGroup.groupNumber} — official places 1–32`
                  : `Stage ${stage + 1} · Group ${visibleGroup.groupNumber} — ${visibleGroup.roundsPlayed}/${visibleGroup.totalRounds} rounds`
              }
            >
              <div className="table-wrap">
                <table className="data-table">
                  <thead>
                    <tr>
                      <th scope="col" className="numeric">Rank</th>
                      <th scope="col">Card</th>
                      <th scope="col">Color</th>
                      <th scope="col">Source</th>
                      <th scope="col" className="numeric">Score</th>
                      <th scope="col" className="numeric">Base</th>
                      <th scope="col" className="numeric">Round W</th>
                      {!visibleGroup.isFinal ? <th scope="col">Status</th> : null}
                    </tr>
                  </thead>
                  <tbody>
                    {visibleGroup.standings.map((row, index) => {
                      const qualifying = !visibleGroup.isFinal && row.groupRank <= CHAOS_ADVANCING;
                      const showCut = !visibleGroup.isFinal && index === CHAOS_ADVANCING - 1;
                      return (
                        <Fragment key={row.saveAthleteId}>
                          <tr key={row.saveAthleteId} className={qualifying ? 'live-qualifying-row' : undefined}>
                            <td className="numeric">{row.groupRank}</td>
                            <td>
                              <AthleteLink saveId={saveId} athleteId={row.saveAthleteId} name={row.name} />
                            </td>
                            <td>{visibleGroup.entrants.find((entry) => entry.saveAthleteId === row.saveAthleteId)?.sportingColorName ?? '—'}</td>
                            <td>{visibleGroup.entrants.find((entry) => entry.saveAthleteId === row.saveAthleteId)?.sourceLabel ?? '—'}</td>
                            <td className="numeric">{formatChaosPoints(row.groupScoreThousandths)}</td>
                            <td className="numeric">{formatChaosPoints(row.baseScoreThousandths)}</td>
                            <td className="numeric">{row.roundWins}</td>
                            {!visibleGroup.isFinal ? (
                              <td>
                                {row.isAdvanced ? (
                                  <span className="badge badge-ready">Advances</span>
                                ) : (
                                  <span className="badge badge-wait">Eliminated</span>
                                )}
                              </td>
                            ) : null}
                          </tr>
                          {showCut && visibleGroup.cutoff ? (
                            <tr key={`cut-${row.saveAthleteId}`}>
                              <td colSpan={8} className="live-cut-cell">
                                <span className="live-cut-label">
                                  {chaosCutLabel(visibleGroup.cutoff.isProvisional)} · P{CHAOS_ADVANCING}{' '}
                                  {visibleGroup.cutoff.p16.name} {formatChaosPoints(visibleGroup.cutoff.p16.groupScoreThousandths)} vs P
                                  {CHAOS_ADVANCING + 1} {visibleGroup.cutoff.p17.name}{' '}
                                  {formatChaosPoints(visibleGroup.cutoff.p17.groupScoreThousandths)} (gap{' '}
                                  {formatChaosPoints(visibleGroup.cutoff.gapThousandths)})
                                </span>
                              </td>
                            </tr>
                          ) : null}
                        </Fragment>
                      );
                    })}
                  </tbody>
                </table>
              </div>
              <p className="muted small">
                {visibleGroup.isFinal
                  ? 'The Final holds official places 1–32; no further cutoff applies.'
                  : visibleGroup.standingsProvisional
                    ? 'Cutoff is provisional until all 16 rounds complete; the persisted table decides who advances.'
                    : 'Cutoff is official: the top 16 advance to the next stage.'}{' '}
                Group scores reset every stage.
              </p>
            </Card>
          </>
        )}
      </div>
    </div>
  );
}
