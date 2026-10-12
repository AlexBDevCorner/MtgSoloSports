import { useCallback, useEffect, useMemo, useState } from 'react';
import { apiErrorMessage } from '../../shared/api/http';
import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import {
  fetchEventRound,
  fetchEventRounds,
  fetchEventTeamStandings,
  fetchSeasonEvents,
  playEventRound,
  runEventRemaining,
  type EventProgress,
  type EventRoundView,
  type EventTeamStandings,
  type PlayedRound,
  type SeasonEventSummary,
} from '../events/eventsApi';
import { EVENT_TITLES, isTeamEvent, progressLabel, resultsTarget, type EventKey } from '../events/eventModel';
import { projectRevealedTeamStandings } from '../events/teamStandingsProjection';
import { optionalDraw, optionalTournament } from '../cups/typeCupTournamentApi';
import type { TypeCupDraw, TypeCupTournament } from '../cups/typeCupTournamentApi';
import { TypeCupLiveStages } from './typeCupLiveStages';
import {
  buildTypeCupStages,
  canonicalStageKey,
  chronologicalStageRounds,
  selectedStageKey,
  stageKeyEquals,
  stageLocalRound,
  stageRoundAccessibleLabel,
  type TypeCupStageInfo,
  type TypeCupStageKey,
} from './typeCupLiveModel';
import { RoundReveal } from '../reveal/RoundReveal';
import type { RevealPlacement } from '../reveal/types';
import { Link } from '../routing/router';
import { cupsPath, dashboardPath, qualifierLivePath, qualifiersPath, standingsPath } from '../routing/routes';
import { fetchSeasonProgress } from '../dashboard/dashboardApi';
import { runRemainingQualifiers } from '../qualifiers/qualifierApi';
import { nextQualifierLiveParam } from '../qualifiers/qualifierModel';
import './LivePage.css';

/** Display-only projection of a fixed-point thousandths value (no sporting math). */
function formatPoints(thousandths: number): string {
  return (thousandths / 1000).toFixed(3);
}

/** MSS-079: the backend rejects re-execution of a completed Cup with 409. */
function isAlreadyCompleteMessage(message: string): boolean {
  const normalized = message.toLowerCase();
  return normalized.includes('already been resolved') || normalized.includes('already complete');
}

/** MSS-079: never surface an internal exception for a completed Cup; point at replay. */
function friendlyCompleteMessage(message: string): string {
  if (isAlreadyCompleteMessage(message)) {
    return `${message} The completed rounds stay replayable below; continue on the Dashboard for the next season.`;
  }
  return message;
}

/**
 * Live view of a postseason event played in rounds (qualifier and Cup
 * events). Each Next Round simulates exactly one round on the backend and
 * reveals the persisted result; nothing is simulated or recomputed here.
 */
export function LiveEventView({
  saveId,
  event,
  season,
  progress,
  urlGroup,
  urlRound,
  urlTournamentPhase,
  urlQualificationGroup,
  onSelectRound,
  onMutated,
}: {
  saveId: string;
  event: EventKey;
  season: number;
  /** Status progress while this event is the next lifecycle step; shapes the view before round 1. */
  progress: EventProgress | null;
  urlGroup: number | null;
  urlRound: number | null;
  /** MSS-079 Type Cup tournament stage from the URL; null keeps legacy URLs working. */
  urlTournamentPhase?: number | null;
  urlQualificationGroup?: number | null;
  /** Selects a played round in the URL; a null round follows the latest round. */
  onSelectRound: (
    group: number | null,
    round: number | null,
    stage?: { tournamentPhase: number; qualificationGroup: number } | null,
  ) => void;
  onMutated: () => void;
}) {
  const [summary, setSummary] = useState<SeasonEventSummary | null>(null);
  const [rounds, setRounds] = useState<PlayedRound[]>([]);
  const [roundView, setRoundView] = useState<EventRoundView | null>(null);
  const [teams, setTeams] = useState<EventTeamStandings | null>(null);
  const [tournament, setTournament] = useState<TypeCupTournament | null>(null);
  const [draw, setDraw] = useState<TypeCupDraw | null>(null);
  const [lastStage, setLastStage] = useState<string | null>(null);
  // Team totals before the shown round, tagged with the round they belong to.
  const [baseline, setBaseline] = useState<{
    group: number;
    round: number;
    tournamentPhase: number | null;
    qualificationGroup: number | null;
    standings: EventTeamStandings;
  } | null>(null);
  const [revealed, setRevealed] = useState<readonly RevealPlacement[]>([]);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // MSS-078 correction: stage-scoped starting field for a pending stage with
  // no persisted round (e.g. Final finalists at zero before Round 1).
  // Tagged with the stage key so a stale stage never renders under a new
  // heading. Null while the stage has rounds (reveal/baseline owns standings)
  // or while the pending roster is still loading/empty.
  const [pendingStageStandings, setPendingStageStandings] = useState<{
    stageKey: string;
    standings: EventTeamStandings;
  } | null>(null);
  const [revision, setRevision] = useState(0);
  // MSS-078: two-step confirmation for the tournament-wide fast-forward so a
  // single click can never silently finish every leftover group and the Final.
  const [confirmEntire, setConfirmEntire] = useState(false);
  const [stageFinishing, setStageFinishing] = useState<number | null>(null);
  // MSS-069 correction: the 17-event next-qualifier/fast-forward flow is
  // tiered-only. v1 saves resolve a single 32-athlete Superleague qualifier
  // under the same `qualifier` event key, so the CTA must stay hidden there.
  // Derived from the event season's league data (never league-name parsing),
  // matching QualifiersPage/SeasonFlow tier detection. Defaults to false
  // (legacy branch) so a failed/slow fetch never falsely claims "1 of 17".
  const [isTiered, setIsTiered] = useState(false);
  const team = isTeamEvent(event);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    const isTypeCup = event === 'type-cup-team';
    const needsTier = event === 'qualifier';
    Promise.all([
      fetchSeasonEvents(saveId, season, controller.signal).catch(() => [] as SeasonEventSummary[]),
      fetchEventRounds(saveId, season, event, controller.signal).catch(() => [] as PlayedRound[]),
      team ? fetchEventTeamStandings(saveId, season, event, controller.signal).catch(() => null) : Promise.resolve(null),
      isTypeCup ? optionalTournament(saveId, season, controller.signal).catch(() => null) : Promise.resolve(null),
      isTypeCup ? optionalDraw(saveId, season, controller.signal).catch(() => null) : Promise.resolve(null),
      needsTier ? fetchSeasonProgress(saveId, season, controller.signal).catch(() => null) : Promise.resolve(null),
    ])
      .then(([events, played, standings, tournamentResult, drawResult, seasonProgress]) => {
        setSummary(events.find((entry) => entry.event === event) ?? null);
        setRounds(played);
        setTeams(standings);
        setTournament(tournamentResult);
        setDraw(drawResult);
        if (needsTier && seasonProgress) {
          setIsTiered(
            seasonProgress.leagues.some(
              (league) =>
                league.leagueLevel === 'Feeder2' ||
                league.leagueLevel === 'Feeder3' ||
                league.feederDivision === 2 ||
                league.feederDivision === 3,
            ),
          );
        } else {
          setIsTiered(false);
        }
        setLoading(false);
      })
      .catch(() => setLoading(false));
    return () => controller.abort();
  }, [saveId, season, event, team, revision]);

  const isTypeCupEvent = event === 'type-cup-team';
  const urlStage: TypeCupStageKey | null =
    urlTournamentPhase !== null &&
    urlTournamentPhase !== undefined &&
    urlQualificationGroup !== null &&
    urlQualificationGroup !== undefined
      ? { tournamentPhase: urlTournamentPhase, qualificationGroup: urlQualificationGroup }
      : null;

  const shape = summary ?? progress;
  // MSS-069: the Superleague qualifier is one 16-round event. The phase-wide
  // 272-round total lives on the qualifier overview only and must never appear
  // under this individual event title (e.g. "Round 16 / 272").
  const totalRounds = event === 'qualifier' ? 16 : (shape?.totalRounds ?? (team ? 32 : 16));
  const roundsPerGroup = event === 'qualifier' ? 16 : (shape?.roundsPerGroup ?? (team ? 8 : 16));
  const groupCount = event === 'qualifier' ? 1 : (shape?.groupCount ?? (team ? 4 : 1));
  const complete = summary?.isComplete ?? false;
  // Type Cup tournaments span several 32-round stages in canonical order;
  // the backend progress cursor names the current stage explicitly.
  const isTypeTournamentLive =
    event === 'type-cup-team' &&
    ((tournament ? !tournament.isDirectFinal : false) ||
      (draw ? !draw.isDirectFinal : false) ||
      (progress?.qualificationGroupCount ?? 0) > 0);
  // Rounds may only be played for the save's next lifecycle event; playing any
  // other event would move the RNG under a partly played one (the backend
  // refuses it too).
  const playable = progress !== null && progress.sourceSeasonNumber === season && !complete;
  // MSS-079: once the tournament is complete the backend progress cursor is
  // gone (the next legal action is StartNextSeason). Never keep showing the
  // stale in-progress stage after that; replay selection resets instead.
  const stageProgressText = complete ? null : (progress?.tournamentStage ?? lastStage);
  const progressText = stageProgressText ?? progressLabel({ roundsPlayed: rounds.length, totalRounds, groupCount, roundsPerGroup });

  // MSS-078: per-stage workspace. Each qualification group and the Final is
  // its own stage with stage-local 1..32 rounds; `group` stays the squad
  // rank group inside a stage and never names a qualification group.
  const qualGroupCount =
    tournament?.qualificationGroupCount ?? draw?.qualificationGroupCount ?? progress?.qualificationGroupCount ?? 0;
  const isDirectFinalType = (tournament ? tournament.isDirectFinal : false) || (draw ? draw.isDirectFinal : false);
  const stageTotal = groupCount * roundsPerGroup > 0 ? groupCount * roundsPerGroup : 32;
  const progressStage: TypeCupStageKey | null =
    !complete && progress?.tournamentPhase !== null && progress?.tournamentPhase !== undefined &&
    progress?.qualificationGroup !== null && progress?.qualificationGroup !== undefined
      ? { tournamentPhase: progress.tournamentPhase, qualificationGroup: progress.qualificationGroup }
      : null;
  const canonicalStage: TypeCupStageKey | null = isTypeTournamentLive
    ? canonicalStageKey({ complete, progressStage, rounds })
    : null;
  const doneQualGroups = new Set((tournament?.qualificationGroups ?? []).map((g) => g.qualificationGroup));
  const finalComplete = Boolean(tournament?.final);
  const typeCupStages: TypeCupStageInfo[] = isTypeTournamentLive
    ? buildTypeCupStages({
        qualificationGroupCount: qualGroupCount,
        isDirectFinal: isDirectFinalType,
        isLegacy: false,
        rounds,
        doneQualificationGroups: doneQualGroups,
        finalComplete,
        activeStage: progressStage,
        tournamentComplete: complete,
        squadGroupCount: groupCount,
        roundsPerGroup,
      })
    : [];
  const selectedTypeStage: TypeCupStageKey | null = isTypeTournamentLive
    ? selectedStageKey({ urlStage, canonicalStage, stages: typeCupStages, rounds })
    : null;
  const selectedStageInfo = selectedTypeStage
    ? (typeCupStages.find((s) => stageKeyEquals(s.key, selectedTypeStage)) ?? null)
    : null;
  const stageOrdered = selectedTypeStage && isTypeTournamentLive
    ? chronologicalStageRounds(rounds, selectedTypeStage, roundsPerGroup)
    : [];
  const stagePlayed = stageOrdered.length;
  const canonicalStageInfo = canonicalStage
    ? (typeCupStages.find((s) => stageKeyEquals(s.key, canonicalStage)) ?? null)
    : null;
  const viewingOtherStage = Boolean(
    isTypeTournamentLive && selectedTypeStage && canonicalStage && !stageKeyEquals(selectedTypeStage, canonicalStage),
  );

  const selected: PlayedRound | null = useMemo(() => {
    if (rounds.length === 0) {
      return null;
    }
    // MSS-078: tournament stages own their rounds. The selected stage comes
    // from the explicit URL stage or the canonical cursor; the round is the
    // latest in that stage unless the URL names an explicit squad-local round.
    if (isTypeTournamentLive && selectedTypeStage) {
      const ordered = chronologicalStageRounds(rounds, selectedTypeStage, roundsPerGroup);
      if (urlRound !== null) {
        const wantedGroup = team ? urlGroup : null;
        const staged = ordered.find((e) => e.round.round === urlRound && e.round.group === wantedGroup);
        if (staged) {
          return staged.round;
        }
        // MSS-079 correction: an explicit stage+round that was never persisted
        // must stay missing so the fetch surfaces a 404. Only genuinely
        // stage-less legacy URLs may fall back; never substitute another
        // stage for an explicit stage URL.
        return {
          group: wantedGroup,
          round: urlRound,
          tournamentPhase: selectedTypeStage.tournamentPhase,
          qualificationGroup: selectedTypeStage.qualificationGroup,
        };
      }
      const last = ordered[ordered.length - 1];
      return last ? last.round : null;
    }
    const wantedGroup = team ? urlGroup : null;
    if (urlRound !== null) {
      if (isTypeCupEvent && urlStage) {
        const staged = rounds.find(
          (r) =>
            r.round === urlRound &&
            r.group === wantedGroup &&
            (r.tournamentPhase ?? null) === urlStage.tournamentPhase &&
            (r.qualificationGroup ?? null) === urlStage.qualificationGroup,
        );
        if (staged) {
          return staged;
        }
        // MSS-079 correction: an explicit stage+round that was never persisted
        // must stay missing so the fetch surfaces a 404. Only genuinely
        // stage-less legacy URLs may fall back; never substitute another
        // stage for an explicit stage URL.
        return {
          group: wantedGroup,
          round: urlRound,
          tournamentPhase: urlStage.tournamentPhase,
          qualificationGroup: urlStage.qualificationGroup,
        };
      }
      if (!isTypeCupEvent) {
        const match = rounds.find((r) => r.round === urlRound && r.group === wantedGroup);
        if (match) {
          return match;
        }
      } else {
        // Legacy Type Cup URL without a stage: an exact single match wins;
        // otherwise follow the latest persisted stage so old links never
        // silently replay the wrong qualification group (the fetch surfaces
        // the ambiguity as a clear error when nothing is uniquely addressable).
        const candidates = rounds.filter((r) => r.round === urlRound && r.group === wantedGroup);
        if (candidates.length === 1) {
          return candidates[0]!;
        }
        if (candidates.length > 1) {
          return candidates[candidates.length - 1]!;
        }
      }
      const fallback = rounds.find((r) => r.round === urlRound && r.group === wantedGroup);
      if (fallback) {
        return fallback;
      }
    }
    return rounds[rounds.length - 1]!;
    // selectedTypeStage/stage rounds derive from rounds+URL stage; canonical
    // progress (progressStage text) does not change selection identity.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rounds, urlRound, urlGroup, team, isTypeCupEvent, urlTournamentPhase, urlQualificationGroup, isTypeTournamentLive, selectedTypeStage?.tournamentPhase, selectedTypeStage?.qualificationGroup, roundsPerGroup]);

  useEffect(() => {
    if (!selected) {
      // MSS-078 correction: a pending stage has no persisted round. Clear the
      // previous stage's reveal/baseline so it never lingers under the new
      // stage heading (stage isolation, fresh Final scores).
      setRoundView(null);
      setBaseline(null);
      setRevealed([]);
      setError(null);
      return;
    }
    const selectedStage =
      selected.tournamentPhase !== null &&
      selected.tournamentPhase !== undefined &&
      selected.qualificationGroup !== null &&
      selected.qualificationGroup !== undefined
        ? { tournamentPhase: selected.tournamentPhase, qualificationGroup: selected.qualificationGroup }
        : (urlStage ?? null);
    if (
      roundView &&
      roundView.roundNumber === selected.round &&
      roundView.group === selected.group &&
      (roundView.tournamentPhase ?? null) === (selectedStage?.tournamentPhase ?? null) &&
      (roundView.qualificationGroup ?? null) === (selectedStage?.qualificationGroup ?? null)
    ) {
      return;
    }
    // MSS-078 correction: switching stages must never display the previous
    // stage's persisted reveal under the new heading, even while the new
    // fetch is pending. Clear upfront; the catch below also clears on any
    // failure (404 or otherwise) so a stale reveal can never linger.
    setRoundView(null);
    setBaseline(null);
    setRevealed([]);
    const controller = new AbortController();
    setError(null);
    const wantedRound = selected.round;
    const wantedGroup = selected.group;
    const wantedPhase = selectedStage?.tournamentPhase ?? null;
    const wantedQual = selectedStage?.qualificationGroup ?? null;
    fetchEventRound(saveId, season, event, selected.round, selected.group, controller.signal, selectedStage)
      .then((view) => {
        if (controller.signal.aborted) {
          return;
        }
        // Guard against a late response for a previously selected stage
        // overwriting the freshly cleared workspace.
        if (
          view.roundNumber !== wantedRound ||
          (view.group ?? null) !== (wantedGroup ?? null) ||
          (view.tournamentPhase ?? null) !== wantedPhase ||
          (view.qualificationGroup ?? null) !== wantedQual
        ) {
          return;
        }
        setRoundView(view);
      })
      .catch((failure: unknown) => {
        if (failure instanceof DOMException && failure.name === 'AbortError') {
          return;
        }
        if (controller.signal.aborted) {
          return;
        }
        setError(apiErrorMessage(failure));
        // MSS-078 correction: any fetch failure must leave no stale reveal.
        // The workspace was already cleared upfront; ensure it stays cleared.
        setRoundView(null);
        setBaseline(null);
        setRevealed([]);
      });
    return () => controller.abort();
    // roundView is the cache this effect fills; re-running on it would loop.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [saveId, season, event, selected]);

  const shownGroup = team ? (roundView?.group ?? null) : null;
  const shownRound = team ? (roundView?.roundNumber ?? null) : null;
  const shownPhase = roundView?.tournamentPhase ?? null;
  const shownQual = roundView?.qualificationGroup ?? null;
  useEffect(() => {
    if (shownGroup === null || shownRound === null) {
      return;
    }
    const controller = new AbortController();
    const stage =
      shownPhase !== null && shownPhase !== undefined && shownQual !== null && shownQual !== undefined
        ? { tournamentPhase: shownPhase, qualificationGroup: shownQual }
        : null;
    const wantedGroup = shownGroup;
    const wantedRound = shownRound;
    const wantedPhase = stage?.tournamentPhase ?? null;
    const wantedQual = stage?.qualificationGroup ?? null;
    fetchEventTeamStandings(saveId, season, event, controller.signal, { group: shownGroup, round: shownRound }, stage)
      .then((standings) => {
        if (controller.signal.aborted) {
          return;
        }
        setBaseline({
          group: wantedGroup,
          round: wantedRound,
          tournamentPhase: wantedPhase,
          qualificationGroup: wantedQual,
          standings,
        });
      })
      .catch(() => undefined);
    return () => controller.abort();
  }, [saveId, season, event, shownGroup, shownRound, shownPhase, shownQual]);

  // MSS-078 correction: a pending stage with no persisted round still needs
  // its stage-scoped starting field (Final finalists at zero once quals are
  // complete). Fetch the stage roster without a before-round cursor; never
  // fall back to unscoped tournament-wide totals.
  const pendingStage: TypeCupStageKey | null =
    isTypeTournamentLive && selectedTypeStage && !selected ? selectedTypeStage : null;
  const pendingStageKey = pendingStage ? `${pendingStage.tournamentPhase}/${pendingStage.qualificationGroup}` : null;
  useEffect(() => {
    if (!pendingStage || !pendingStageKey) {
      setPendingStageStandings(null);
      return;
    }
    const controller = new AbortController();
    const key = pendingStageKey;
    const stage = { tournamentPhase: pendingStage.tournamentPhase, qualificationGroup: pendingStage.qualificationGroup };
    fetchEventTeamStandings(saveId, season, event, controller.signal, undefined, stage)
      .then((standings) => {
        if (controller.signal.aborted) {
          return;
        }
        setPendingStageStandings({ stageKey: key, standings });
      })
      .catch(() => {
        if (controller.signal.aborted) {
          return;
        }
        setPendingStageStandings(null);
      });
    return () => controller.abort();
    // pendingStage object identity changes per render; depend on its key parts.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [saveId, season, event, pendingStageKey, pendingStage?.tournamentPhase, pendingStage?.qualificationGroup]);

  const refreshAfter = useCallback(() => {
    setRevision((value) => value + 1);
    onMutated();
  }, [onMutated]);

  async function handleNext(): Promise<void> {
    if (busy) {
      return;
    }
    setBusy(true);
    setError(null);
    setConfirmEntire(false);
    try {
      const result = await playEventRound(saveId, event);
      setRoundView(result.round);
      if (result.tournamentStage) {
        setLastStage(result.tournamentStage);
      }
      onSelectRound(
        result.round.group,
        result.round.roundNumber,
        result.tournamentPhase !== null &&
          result.tournamentPhase !== undefined &&
          result.qualificationGroup !== null &&
          result.qualificationGroup !== undefined
          ? { tournamentPhase: result.tournamentPhase, qualificationGroup: result.qualificationGroup }
          : (result.round.tournamentPhase !== null &&
                result.round.tournamentPhase !== undefined &&
                result.round.qualificationGroup !== null &&
                result.round.qualificationGroup !== undefined
              ? {
                  tournamentPhase: result.round.tournamentPhase,
                  qualificationGroup: result.round.qualificationGroup,
                }
              : null),
      );
      refreshAfter();
    } catch (failure) {
      const message = apiErrorMessage(failure);
      // MSS-079: a completed Cup rejects re-execution with 409. Refresh the
      // authoritative status so Live converges to completion/replay instead of
      // confidently keeping stale playable controls.
      if (isAlreadyCompleteMessage(message)) {
        setLastStage(null);
        setRoundView(null);
        onSelectRound(null, null, null);
        refreshAfter();
      }
      setError(friendlyCompleteMessage(message));
    } finally {
      setBusy(false);
    }
  }

  // MSS-078: stage-scoped fast-forward. Plays the remaining rounds of the
  // canonical stage only (never the next group/Final) by reusing the tested
  // single-step endpoint in canonical order. Resume-safe: each round persists
  // before the next is played; a failure stops with prior rounds intact.
  async function handleFinishStage(): Promise<void> {
    if (busy || !isTypeTournamentLive || !canonicalStage) {
      return;
    }
    const canonicalSnapshot = canonicalStage;
    const playedInStage = rounds.filter(
      (r) => (r.tournamentPhase ?? null) === canonicalSnapshot.tournamentPhase && (r.qualificationGroup ?? null) === canonicalSnapshot.qualificationGroup,
    ).length;
    const remaining = stageTotal - playedInStage;
    if (remaining <= 0) {
      refreshAfter();
      return;
    }
    setBusy(true);
    setError(null);
    setConfirmEntire(false);
    try {
      let last: Awaited<ReturnType<typeof playEventRound>> | null = null;
      for (let i = 0; i < remaining; i += 1) {
        setStageFinishing(i + 1);
        const result = await playEventRound(saveId, event);
        last = result;
        if (result.tournamentStage) {
          setLastStage(result.tournamentStage);
        }
        const resultStage =
          result.tournamentPhase !== null && result.tournamentPhase !== undefined &&
          result.qualificationGroup !== null && result.qualificationGroup !== undefined
            ? { tournamentPhase: result.tournamentPhase, qualificationGroup: result.qualificationGroup }
            : (result.round.tournamentPhase !== null && result.round.tournamentPhase !== undefined &&
                result.round.qualificationGroup !== null && result.round.qualificationGroup !== undefined
                ? { tournamentPhase: result.round.tournamentPhase, qualificationGroup: result.round.qualificationGroup }
                : null);
        // Stop at the stage boundary: never play the next group/Final here.
        if (resultStage && !stageKeyEquals(resultStage, canonicalSnapshot)) {
          break;
        }
        if (result.isComplete) {
          break;
        }
      }
      if (last) {
        setRoundView(last.round);
        const stage =
          last.tournamentPhase !== null && last.tournamentPhase !== undefined &&
          last.qualificationGroup !== null && last.qualificationGroup !== undefined
            ? { tournamentPhase: last.tournamentPhase, qualificationGroup: last.qualificationGroup }
            : (last.round.tournamentPhase !== null && last.round.tournamentPhase !== undefined &&
                last.round.qualificationGroup !== null && last.round.qualificationGroup !== undefined
                ? { tournamentPhase: last.round.tournamentPhase, qualificationGroup: last.round.qualificationGroup }
                : null);
        onSelectRound(last.round.group, last.round.roundNumber, stage);
      }
      refreshAfter();
    } catch (failure) {
      const message = apiErrorMessage(failure);
      if (isAlreadyCompleteMessage(message)) {
        setLastStage(null);
        setRoundView(null);
        onSelectRound(null, null, null);
        refreshAfter();
      }
      setError(friendlyCompleteMessage(message));
    } finally {
      setBusy(false);
      setStageFinishing(null);
    }
  }

  function handleSelectTypeStage(stage: TypeCupStageKey): void {
    const ordered = chronologicalStageRounds(rounds, stage, roundsPerGroup);
    const last = ordered[ordered.length - 1];
    if (last) {
      onSelectRound(last.round.group, last.round.round, stage);
    } else {
      onSelectRound(null, null, stage);
    }
  }

  async function handleRunRemaining(): Promise<void> {
    if (busy) {
      return;
    }
    // MSS-078: tournament-wide fast-forward needs an explicit second click so
    // it can never be mistaken for the stage-scoped action.
    if (isTypeTournamentLive && !confirmEntire) {
      setConfirmEntire(true);
      return;
    }
    setBusy(true);
    setError(null);
    try {
      // Backend persisted progression in canonical order: every remaining
      // qualification group then the Final. Never simulated here.
      await runEventRemaining(saveId, event);
      setRoundView(null);
      setBaseline(null);
      setLastStage(null);
      setConfirmEntire(false);
      onSelectRound(null, null, null);
      refreshAfter();
    } catch (failure) {
      const message = apiErrorMessage(failure);
      if (isAlreadyCompleteMessage(message)) {
        setLastStage(null);
        setRoundView(null);
        onSelectRound(null, null, null);
        refreshAfter();
      }
      setError(friendlyCompleteMessage(message));
    } finally {
      setBusy(false);
    }
  }

  async function handleRunAllQualifiers(): Promise<void> {
    if (busy) {
      return;
    }
    setBusy(true);
    setError(null);
    try {
      // MSS-069 fast-forward: persists every remaining qualifier event in
      // canonical order (resume-safe, skips completed work). Never simulated here.
      await runRemainingQualifiers(saveId);
      setRoundView(null);
      setLastStage(null);
      onSelectRound(null, null);
      refreshAfter();
    } catch (failure) {
      setError(apiErrorMessage(failure));
    } finally {
      setBusy(false);
    }
  }

  // The sidebar standings follow the reveal: totals before the shown round plus
  // the round points of the athletes revealed so far. The persisted final
  // ranking only appears once the event's last round is fully revealed.
  const lastRound = rounds.at(-1) ?? null;
  const shownIsLast =
    roundView !== null &&
    lastRound !== null &&
    roundView.group === lastRound.group &&
    roundView.roundNumber === lastRound.round &&
    (roundView.tournamentPhase ?? null) === (lastRound.tournamentPhase ?? null) &&
    (roundView.qualificationGroup ?? null) === (lastRound.qualificationGroup ?? null);
  const fullyRevealed = roundView !== null && revealed.length >= roundView.placements.length;
  const showFinal = teams !== null && teams.isFinal && shownIsLast && fullyRevealed;
  const baselineReady =
    baseline !== null &&
    baseline.group === shownGroup &&
    baseline.round === shownRound &&
    (baseline.tournamentPhase ?? null) === (shownPhase ?? null) &&
    (baseline.qualificationGroup ?? null) === (shownQual ?? null);
  const revealedTeams = useMemo(
    () => (baseline ? projectRevealedTeamStandings(baseline.standings.teams, baseline.standings.members, revealed) : []),
    [baseline, revealed],
  );

  const selectedOverall =
    isTypeTournamentLive && selected
      ? stageLocalRound(selected.group ?? 1, selected.round, roundsPerGroup)
      : null;
  const shownOverall =
    isTypeTournamentLive && roundView ? stageLocalRound(roundView.group ?? 1, roundView.roundNumber, roundsPerGroup) : null;
  const stageProgressLine = isTypeTournamentLive && selectedStageInfo
    ? `${selectedStageInfo.label} · Round ${selectedOverall ?? stagePlayed} / ${stageTotal}`
    : null;
  // Generic (non-tournament) squad navigation stays untouched.
  const visibleGroup = selected?.group ?? (team ? 1 : null);
  const groupsPlayed = !isTypeTournamentLive && team ? Array.from(new Set(rounds.map((r) => r.group ?? 1))) : [];
  const genericPills: PlayedRound[] = isTypeTournamentLive ? [] : rounds.filter((r) => r.group === visibleGroup);
  const title = `${EVENT_TITLES[event]} · Season ${season}`;
  const resultsPath = resultsTarget(event) === 'standings' ? standingsPath(saveId) : cupsPath(saveId);
  // MSS-069: after the 32-athlete Superleague qualifier the canonical Live
  // order continues with the eight F1↔F2 feeder qualifiers (White first) —
  // but only for tiered saves. v1 saves share the same `qualifier` event key
  // for their sole Superleague qualifier, so the 17-event CTA stays hidden
  // there (legacy Dashboard/results branch below).
  // The next-qualifier link replays the same displayed season, so it stays
  // available on historical pages. The run-all fast-forward is unscoped on
  // the backend (it resolves the latest pending postseason), so it must only
  // appear while the displayed season is the save's current qualifier phase.
  const isSuperleagueLive = event === 'qualifier';
  const nextQualifierParam = isSuperleagueLive && isTiered ? nextQualifierLiveParam('superleague') : null;
  const canRunAllFromComplete =
    isSuperleagueLive && isTiered && progress !== null && progress.sourceSeasonNumber === season;

  const nextRoundLabel = isTypeTournamentLive
    ? viewingOtherStage && canonicalStageInfo
      ? `Next Round · ${canonicalStageInfo.label}`
      : selectedStageInfo
        ? `Next Round · ${selectedStageInfo.label}`
        : 'Next Round'
    : 'Next Round';
  const finishStageLabel = isTypeTournamentLive && canonicalStageInfo
    ? stageFinishing !== null
      ? `Finishing ${canonicalStageInfo.label}… ${stageFinishing}`
      : `Finish ${canonicalStageInfo.label}`
    : 'Finish current stage';
  const finishStageRemaining = isTypeTournamentLive && canonicalStage
    ? stageTotal -
      rounds.filter(
        (r) => (r.tournamentPhase ?? null) === canonicalStage.tournamentPhase && (r.qualificationGroup ?? null) === canonicalStage.qualificationGroup,
      ).length
    : 0;

  return (
    <div className="live-layout">
      <aside className="live-sidebar" aria-label="Event management">
        <Card eyebrow="Postseason event" title={title}>
          <p className="muted small live-count">
            {isTypeTournamentLive && stageProgressLine ? stageProgressLine : progressText}
            {isTypeTournamentLive && canonicalStageInfo && !complete && viewingOtherStage
              ? ` · Active: ${canonicalStageInfo.label}`
              : null}
          </p>
          {complete ? (
            isSuperleagueLive && nextQualifierParam ? (
              <Notice tone="info" title={`${EVENT_TITLES[event]} complete`}>
                <p className="live-buttons">
                  <Link
                    to={qualifierLivePath(saveId, nextQualifierParam, season, null)}
                    className="primary-button"
                  >
                    Play next qualifier on Live
                  </Link>
                  <Link to={qualifiersPath(saveId, { season })} className="ghost-button">
                    All 17 qualifiers
                  </Link>
                  <Link to={resultsPath} className="ghost-button">
                    View results
                  </Link>
                </p>
                <p className="live-buttons">
                  {canRunAllFromComplete ? (
                    <button
                      type="button"
                      className="ghost-button"
                      disabled={busy}
                      title="Persists every remaining qualifier in canonical order (resume-safe)."
                      onClick={() => {
                        void handleRunAllQualifiers();
                      }}
                    >
                      {busy ? 'Running…' : 'Run all remaining qualifiers'}
                    </button>
                  ) : null}
                  <Link to={dashboardPath(saveId)} className="ghost-button">
                    Dashboard
                  </Link>
                </p>
                <p className="muted small">
                  Qualifier 1 of 17 in canonical order — Superleague first, then
                  Feeder 1↔Feeder 2 by color, then Feeder 2↔Feeder 3 by color. No
                  Dashboard trip needed between qualifiers.
                </p>
              </Notice>
            ) : (
              <Notice tone="info" title={`${EVENT_TITLES[event]} complete`}>
                <p className="live-buttons">
                  <Link to={dashboardPath(saveId)} className="primary-button">
                    Continue on the Dashboard
                  </Link>
                  <Link to={resultsPath} className="ghost-button">
                    View results
                  </Link>
                </p>
              </Notice>
            )
          ) : playable ? (
            <div className="live-manage-controls">
              <div className="live-buttons">
                <button
                  type="button"
                  className="primary-button"
                  disabled={busy}
                  aria-busy={busy}
                  title={
                    isTypeTournamentLive && canonicalStageInfo
                      ? `Simulate the next round of ${canonicalStageInfo.label} on the backend.`
                      : 'Simulate the next round on the backend.'
                  }
                  onClick={() => {
                    void handleNext();
                  }}
                >
                  {busy ? 'Simulating…' : nextRoundLabel}
                </button>
                {isTypeTournamentLive ? (
                  <button
                    type="button"
                    className="ghost-button"
                    disabled={busy || finishStageRemaining <= 0}
                    title={
                      canonicalStageInfo
                        ? `Persists the remaining ${finishStageRemaining} rounds of ${canonicalStageInfo.label} only; stops before the next stage.`
                        : 'Persists the remaining rounds of the active stage only.'
                    }
                    onClick={() => {
                      void handleFinishStage();
                    }}
                  >
                    {finishStageLabel}
                  </button>
                ) : null}
                <button
                  type="button"
                  className="ghost-button"
                  disabled={busy}
                  title={
                    isTypeTournamentLive
                      ? 'Tournament-wide fast-forward: persists every remaining qualification group and the Final.'
                      : undefined
                  }
                  onClick={() => {
                    void handleRunRemaining();
                  }}
                >
                  {isTypeTournamentLive ? 'Finish entire Type Cup' : 'Run remaining rounds'}
                </button>
                {isSuperleagueLive && isTiered ? (
                  <button
                    type="button"
                    className="ghost-button"
                    disabled={busy}
                    title="Persists every remaining qualifier in canonical order (resume-safe)."
                    onClick={() => {
                      void handleRunAllQualifiers();
                    }}
                  >
                    {busy ? 'Running…' : 'Run all remaining qualifiers'}
                  </button>
                ) : null}
              </div>
              <p className="muted small">{"Each round is saved and can't be undone."}</p>
              {isTypeTournamentLive && confirmEntire ? (
                <Notice tone="error" title="Finish the entire Type Cup?">
                  <p>
                    This plays every remaining qualification group and the Final. To stop at the
                    end of {canonicalStageInfo?.label ?? 'the current stage'}, use{' '}
                    {canonicalStageInfo ? `Finish ${canonicalStageInfo.label}` : 'Finish current stage'} instead.
                  </p>
                  <p className="live-buttons">
                    <button
                      type="button"
                      className="ghost-button"
                      disabled={busy}
                      onClick={() => {
                        void handleRunRemaining();
                      }}
                    >
                      {busy ? 'Finishing…' : 'Yes, finish entire Type Cup'}
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
              {isSuperleagueLive && nextQualifierParam ? (
                <p className="muted small">
                  After this event, continue directly to the next qualifier on Live — no
                  Dashboard trip needed.{' '}
                  <Link to={qualifierLivePath(saveId, nextQualifierParam, season, null)}>
                    Next qualifier
                  </Link>
                  .
                </p>
              ) : null}
            </div>
          ) : (
            <Notice tone="info" title="Not the next event">
              <p>This event can be played once it is the next step of the season.</p>
              <p className="live-buttons">
                <Link to={dashboardPath(saveId)} className="primary-button">
                  Continue on the Dashboard
                </Link>
              </p>
            </Notice>
          )}
          {error ? (
            <Notice tone="error" title="The round did not complete">
              <p>{error}</p>
              <p>
                <button type="button" className="ghost-button" onClick={() => setError(null)}>
                  Dismiss
                </button>
              </p>
            </Notice>
          ) : null}
          {isTypeTournamentLive && playable && !complete ? (
            <p className="muted small">
              Stages complete in canonical draw order. Finish {canonicalStageInfo?.label ?? 'current stage'} stops
              at this stage; Finish entire Type Cup plays every leftover stage.
            </p>
          ) : null}
        </Card>

        {isTypeTournamentLive ? (
          <TypeCupLiveStages
            saveId={saveId}
            season={season}
            stages={typeCupStages}
            selected={selectedTypeStage}
            canonical={canonicalStage}
            tournament={tournament}
            draw={draw}
            complete={complete}
            onSelectStage={handleSelectTypeStage}
          />
        ) : null}

        <Card
          eyebrow="Rounds"
          title={
            team
              ? isTypeTournamentLive && selectedStageInfo
                ? `${selectedStageInfo.label} · Round ${selectedOverall ?? stagePlayed} / ${stageTotal}`
                : `Group ${visibleGroup ?? 1}`
              : 'Played rounds'
          }
        >
          {isTypeTournamentLive && selectedStageInfo ? (
            <p className="muted small">
              {selectedStageInfo.kind === 'final'
                ? 'Final rounds start fresh at zero. Squad groups are rank positions inside the Final.'
                : `Squad groups are rank positions inside ${selectedStageInfo.label} (Squad #1 vs #1, …), not qualification groups.`}
            </p>
          ) : null}
          {!isTypeTournamentLive && team && groupsPlayed.length > 0 ? (
            <div className="segmented" role="group" aria-label="Groups">
              {groupsPlayed.map((group) => (
                <button
                  key={group}
                  type="button"
                  className={group === visibleGroup ? 'nav-item current' : 'nav-item'}
                  aria-pressed={group === visibleGroup}
                  onClick={() => {
                    const scoped = rounds.filter((r) => r.group === group);
                    const last = scoped.at(-1);
                    if (last) {
                      onSelectRound(group, last.round, null);
                    }
                  }}
                >
                  {group}
                </button>
              ))}
            </div>
          ) : null}
          {isTypeTournamentLive ? (
            stageOrdered.length === 0 ? (
              <p className="muted small">
                {selectedStageInfo?.kind === 'final'
                  ? 'No Final rounds yet. Press Next Round to play Final Round 1 — scores start at zero.'
                  : 'No rounds in this stage yet.'}
              </p>
            ) : (
              <div className="round-pills round-pills-compact" role="group" aria-label={`${selectedStageInfo?.label} rounds in stage order`}>
                {stageOrdered.map(({ round: r, overall }) => {
                  const isCurrent =
                    selected !== null &&
                    r.round === selected.round &&
                    r.group === selected.group &&
                    (r.tournamentPhase ?? null) === (selected.tournamentPhase ?? null) &&
                    (r.qualificationGroup ?? null) === (selected.qualificationGroup ?? null);
                  const accessible = stageRoundAccessibleLabel({
                    overall,
                    total: stageTotal,
                    squadGroup: r.group,
                    roundInGroup: r.round,
                    stageLabel: selectedStageInfo?.label ?? 'Stage',
                  });
                  return (
                    <button
                      key={`${r.tournamentPhase ?? 'x'}/${r.qualificationGroup ?? 'x'}/${r.group ?? 0}-${r.round}`}
                      type="button"
                      className={isCurrent ? 'nav-item current' : 'nav-item'}
                      aria-pressed={isCurrent}
                      aria-label={accessible}
                      title={`${accessible} · Squad #${r.group ?? 1} Round ${r.round} of ${roundsPerGroup}`}
                      onClick={() =>
                        onSelectRound(
                          r.group,
                          r.round,
                          r.tournamentPhase !== null &&
                            r.tournamentPhase !== undefined &&
                            r.qualificationGroup !== null &&
                            r.qualificationGroup !== undefined
                            ? { tournamentPhase: r.tournamentPhase, qualificationGroup: r.qualificationGroup }
                            : null,
                        )
                      }
                    >
                      {overall}
                    </button>
                  );
                })}
              </div>
            )
          ) : genericPills.length === 0 ? (
            <p className="muted small">No rounds played yet.</p>
          ) : (
            <div className="round-pills round-pills-compact" role="group" aria-label="Played rounds">
              {genericPills.map((r) => (
                <button
                  key={`${r.tournamentPhase ?? 'x'}/${r.qualificationGroup ?? 'x'}/${r.group ?? 0}-${r.round}`}
                  type="button"
                  className={
                    selected &&
                    r.round === selected.round &&
                    r.group === selected.group &&
                    (r.tournamentPhase ?? null) === (selected.tournamentPhase ?? null) &&
                    (r.qualificationGroup ?? null) === (selected.qualificationGroup ?? null)
                      ? 'nav-item current'
                      : 'nav-item'
                  }
                  aria-pressed={Boolean(
                    selected &&
                      r.round === selected.round &&
                      r.group === selected.group &&
                      (r.tournamentPhase ?? null) === (selected.tournamentPhase ?? null) &&
                      (r.qualificationGroup ?? null) === (selected.qualificationGroup ?? null),
                  )}
                  onClick={() =>
                    onSelectRound(
                      r.group,
                      r.round,
                      r.tournamentPhase !== null &&
                        r.tournamentPhase !== undefined &&
                        r.qualificationGroup !== null &&
                        r.qualificationGroup !== undefined
                        ? { tournamentPhase: r.tournamentPhase, qualificationGroup: r.qualificationGroup }
                        : null,
                    )
                  }
                >
                  {r.round}
                </button>
              ))}
            </div>
          )}
          {team && roundView && !showFinal ? (
            baselineReady ? (
              <table className="mini-table">
                <caption className="reveal-subhead">
                  {isTypeTournamentLive && selectedStageInfo
                    ? selectedStageInfo.kind === 'final'
                      ? 'Final standings'
                      : `Team standings in ${selectedStageInfo.label}`
                    : 'Team standings so far'}
                  <span className="muted small live-team-caption">
                    {isTypeTournamentLive && selectedStageInfo && shownOverall !== null
                      ? `${selectedStageInfo.label} · Round ${shownOverall} / ${stageTotal} (Squad #${roundView.group ?? 1} · Round ${roundView.roundNumber} of ${roundsPerGroup}) · ${revealed.length}/${roundView.placements.length} revealed`
                      : `${selectedStageInfo?.label ? `${selectedStageInfo.label} · ` : ''}${revealed.length}/${roundView.placements.length} revealed`}
                  </span>
                </caption>
                <thead>
                  <tr>
                    <th scope="col">Team</th>
                    <th scope="col" className="numeric">
                      This round
                    </th>
                    <th scope="col" className="numeric">
                      Points
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {revealedTeams.map((row) => (
                    <tr key={row.teamName}>
                      <td>{row.teamName}</td>
                      <td className="numeric">{row.roundThousandths > 0 ? `+${formatPoints(row.roundThousandths)}` : '—'}</td>
                      <td className="numeric">{formatPoints(row.scoreThousandths)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            ) : null
          ) : isTypeTournamentLive &&
            !roundView &&
            pendingStage &&
            selectedStageInfo &&
            pendingStageStandings &&
            pendingStageStandings.stageKey === pendingStageKey &&
            pendingStageStandings.standings.teams.length > 0 ? (
            <table className="mini-table">
              <caption className="reveal-subhead">
                {selectedStageInfo.kind === 'final' ? 'Final standings' : `Team standings in ${selectedStageInfo.label}`}
                <span className="muted small live-team-caption">
                  {selectedStageInfo.kind === 'final'
                    ? `${selectedStageInfo.label} · starting at zero · ${pendingStageStandings.standings.teams.length} finalists`
                    : `${selectedStageInfo.label} · starting at zero`}
                </span>
              </caption>
              <thead>
                <tr>
                  <th scope="col">Team</th>
                  <th scope="col" className="numeric">
                    Points
                  </th>
                </tr>
              </thead>
              <tbody>
                {pendingStageStandings.standings.teams.map((row) => (
                  <tr key={row.teamName}>
                    <td>{row.teamName}</td>
                    <td className="numeric">{formatPoints(row.scoreThousandths)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          ) : team && teams && (!isTypeTournamentLive || showFinal) ? (
            <table className="mini-table">
              <caption className="reveal-subhead">{teams.isFinal ? 'Team standings' : 'Team standings so far'}</caption>
              <thead>
                <tr>
                  <th scope="col">Team</th>
                  <th scope="col" className="numeric">
                    Points
                  </th>
                </tr>
              </thead>
              <tbody>
                {teams.teams.map((row) => (
                  <tr key={row.teamName}>
                    <td>
                      {row.rank !== null ? `${row.rank}. ` : ''}
                      {row.teamName}
                    </td>
                    <td className="numeric">{formatPoints(row.scoreThousandths)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          ) : null}
        </Card>
      </aside>

      <section className="live-main" aria-label="Event round">
        {loading && !roundView ? (
          <Loading label="Loading event…" />
        ) : roundView ? (
          <RoundReveal
            placements={roundView.placements}
            revealKey={`${event}:${season}:${roundView.tournamentPhase ?? 'x'}:${roundView.qualificationGroup ?? 'x'}:${roundView.group ?? 0}:${roundView.roundNumber}:${roundView.payloadChecksum}`}
            roundLabel={
              isTypeTournamentLive && selectedStageInfo && shownOverall !== null
                ? `${selectedStageInfo.label} · Round ${shownOverall} / ${stageTotal} (Squad #${roundView.group ?? 1} · Round ${roundView.roundNumber} of ${roundsPerGroup}) results`
                : selectedStageInfo
                  ? `${selectedStageInfo.label} results`
                  : 'Round results'
            }
            meta={`Rules v${roundView.rulesVersion} · checksum ${roundView.payloadChecksum.slice(0, 12)}…`}
            autoPlayOnStart={false}
            layout="live"
            saveId={saveId}
            onRevealedChange={setRevealed}
          />
        ) : (
          <Card
            eyebrow="Postseason event"
            title={isTypeTournamentLive && selectedStageInfo ? selectedStageInfo.label : EVENT_TITLES[event]}
          >
            <p className="muted">
              {complete
                ? 'Pick a round to replay it.'
                : isTypeTournamentLive && selectedStageInfo
                  ? selectedStageInfo.kind === 'final'
                    ? 'Final starts fresh at zero. Press Next Round to play Final Round 1.'
                    : `Press Next Round to simulate ${selectedStageInfo.label} Round 1.`
                  : 'Press Next Round to simulate round 1.'}
            </p>
          </Card>
        )}
      </section>
    </div>
  );
}
