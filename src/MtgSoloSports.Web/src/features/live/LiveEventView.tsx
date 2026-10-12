import { useCallback, useEffect, useMemo, useState } from 'react';
import { ApiError, apiErrorMessage } from '../../shared/api/http';
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
import { EVENT_TITLES, isTeamEvent, progressLabel, resultsTarget, roundLabel, type EventKey } from '../events/eventModel';
import { projectRevealedTeamStandings } from '../events/teamStandingsProjection';
import { optionalDraw, optionalTournament } from '../cups/typeCupTournamentApi';
import type { TypeCupDraw, TypeCupTournament } from '../cups/typeCupTournamentApi';
import { TypeCupLiveStages } from './typeCupLiveStages';
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
  const [revision, setRevision] = useState(0);
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
  const urlStage =
    urlTournamentPhase !== null &&
    urlTournamentPhase !== undefined &&
    urlQualificationGroup !== null &&
    urlQualificationGroup !== undefined
      ? { tournamentPhase: urlTournamentPhase, qualificationGroup: urlQualificationGroup }
      : null;

  const selected: PlayedRound | null = useMemo(() => {
    if (rounds.length === 0) {
      return null;
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
    // urlStage is derived from urlTournamentPhase/urlQualificationGroup.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rounds, urlRound, urlGroup, team, isTypeCupEvent, urlTournamentPhase, urlQualificationGroup]);

  useEffect(() => {
    if (!selected) {
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
    const controller = new AbortController();
    setError(null);
    fetchEventRound(saveId, season, event, selected.round, selected.group, controller.signal, selectedStage)
      .then(setRoundView)
      .catch((failure: unknown) => {
        if (!(failure instanceof DOMException && failure.name === 'AbortError')) {
          setError(apiErrorMessage(failure));
          // MSS-079 correction: an explicit but unpersisted stage+round must not
          // keep showing the previous stage's replay alongside the error.
          if (failure instanceof ApiError && failure.status === 404) {
            setRoundView(null);
          }
        }
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
    fetchEventTeamStandings(saveId, season, event, controller.signal, { group: shownGroup, round: shownRound }, stage)
      .then((standings) =>
        setBaseline({
          group: shownGroup,
          round: shownRound,
          tournamentPhase: stage?.tournamentPhase ?? null,
          qualificationGroup: stage?.qualificationGroup ?? null,
          standings,
        }),
      )
      .catch(() => undefined);
    return () => controller.abort();
  }, [saveId, season, event, shownGroup, shownRound, shownPhase, shownQual]);

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

  async function handleRunRemaining(): Promise<void> {
    if (busy) {
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

  const selectedStageForPills =
    selected?.tournamentPhase !== null &&
    selected?.tournamentPhase !== undefined &&
    selected?.qualificationGroup !== null &&
    selected?.qualificationGroup !== undefined
      ? { tournamentPhase: selected.tournamentPhase, qualificationGroup: selected.qualificationGroup }
      : urlStage;
  const visibleGroup = selected?.group ?? (team ? 1 : null);
  const groupsPlayed = team ? Array.from(new Set(rounds.map((r) => r.group ?? 1))) : [];
  const typeStages = isTypeCupEvent
    ? Array.from(
        new Map(
          rounds
            .filter(
              (r) =>
                r.tournamentPhase !== null &&
                r.tournamentPhase !== undefined &&
                r.qualificationGroup !== null &&
                r.qualificationGroup !== undefined,
            )
            .map(
              (r) =>
                [
                  `${r.tournamentPhase}/${r.qualificationGroup}`,
                  { tournamentPhase: r.tournamentPhase!, qualificationGroup: r.qualificationGroup! },
                ] as const,
            ),
        ).values(),
      )
    : [];
  const pills =
    isTypeCupEvent && selectedStageForPills && typeStages.length > 1
      ? rounds.filter(
          (r) =>
            r.group === visibleGroup &&
            (r.tournamentPhase ?? null) === selectedStageForPills.tournamentPhase &&
            (r.qualificationGroup ?? null) === selectedStageForPills.qualificationGroup,
        )
      : rounds.filter((r) => r.group === visibleGroup);
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

  return (
    <div className="live-layout">
      <aside className="live-sidebar" aria-label="Event management">
        <Card eyebrow="Postseason event" title={title}>
          <p className="muted small live-count">{progressText}</p>
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
                  onClick={() => {
                    void handleNext();
                  }}
                >
                  {busy ? 'Simulating…' : 'Next Round'}
                </button>
                <button
                  type="button"
                  className="ghost-button"
                  disabled={busy}
                  onClick={() => {
                    void handleRunRemaining();
                  }}
                >
                  Run remaining rounds
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
              Qualification groups complete in canonical draw order before the Final. Run remaining
              finishes every leftover group and the Final on the backend — no Dashboard round trip
              needed between groups.
            </p>
          ) : null}
        </Card>

        {isTypeTournamentLive ? (
          <TypeCupLiveStages
            saveId={saveId}
            season={season}
            progress={progress}
            tournament={tournament}
            draw={draw}
            lastStage={lastStage}
          />
        ) : null}

        <Card
          eyebrow="Rounds"
          title={
            team
              ? isTypeTournamentLive
                ? `Squad rank group ${visibleGroup ?? 1} · ${stageProgressText ?? progressText}`
                : `Group ${visibleGroup ?? 1}`
              : 'Played rounds'
          }
        >
          {isTypeTournamentLive ? (
            <p className="muted small">
              Rank groups are squad positions (#1 vs #1, #2 vs #2, …) inside the current
              qualification group or Final — not the qualification groups themselves. Stage
              progress above names the active group.
            </p>
          ) : null}
          {isTypeTournamentLive && typeStages.length > 1 ? (
            <div className="segmented" role="group" aria-label="Tournament stages">
              {typeStages.map((stage) => {
                const label =
                  stage.tournamentPhase === 2
                    ? 'Final'
                    : stage.tournamentPhase === 0
                      ? 'Single field'
                      : `Qualification ${stage.qualificationGroup}`;
                const active =
                  selectedStageForPills?.tournamentPhase === stage.tournamentPhase &&
                  selectedStageForPills?.qualificationGroup === stage.qualificationGroup;
                return (
                  <button
                    key={`${stage.tournamentPhase}/${stage.qualificationGroup}`}
                    type="button"
                    className={active ? 'nav-item current' : 'nav-item'}
                    aria-pressed={active}
                    onClick={() => {
                      const last = rounds
                        .filter(
                          (r) =>
                            (r.tournamentPhase ?? null) === stage.tournamentPhase &&
                            (r.qualificationGroup ?? null) === stage.qualificationGroup &&
                            r.group === visibleGroup,
                        )
                        .at(-1);
                      if (last) {
                        onSelectRound(last.group, last.round, stage);
                      } else {
                        onSelectRound(visibleGroup, null, stage);
                      }
                    }}
                  >
                    {label}
                  </button>
                );
              })}
            </div>
          ) : null}
          {team && groupsPlayed.length > 0 ? (
            <div className="segmented" role="group" aria-label="Groups">
              {groupsPlayed.map((group) => (
                <button
                  key={group}
                  type="button"
                  className={group === visibleGroup ? 'nav-item current' : 'nav-item'}
                  aria-pressed={group === visibleGroup}
                  onClick={() => {
                    const scoped =
                      isTypeTournamentLive && selectedStageForPills && typeStages.length > 1
                        ? rounds.filter(
                            (r) =>
                              r.group === group &&
                              (r.tournamentPhase ?? null) === selectedStageForPills.tournamentPhase &&
                              (r.qualificationGroup ?? null) === selectedStageForPills.qualificationGroup,
                          )
                        : rounds.filter((r) => r.group === group);
                    const last = scoped.at(-1);
                    if (last) {
                      const stage =
                        last.tournamentPhase !== null &&
                        last.tournamentPhase !== undefined &&
                        last.qualificationGroup !== null &&
                        last.qualificationGroup !== undefined
                          ? {
                              tournamentPhase: last.tournamentPhase,
                              qualificationGroup: last.qualificationGroup,
                            }
                          : (selectedStageForPills ?? null);
                      onSelectRound(group, last.round, stage);
                    }
                  }}
                >
                  {group}
                </button>
              ))}
            </div>
          ) : null}
          {pills.length === 0 ? (
            <p className="muted small">No rounds played yet.</p>
          ) : (
            <div className="round-pills round-pills-compact" role="group" aria-label="Played rounds">
              {pills.map((r) => (
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
                  Team standings so far
                  <span className="muted small live-team-caption">
                    {`${roundLabel(roundView.group, roundView.roundNumber)} · ${revealed.length}/${roundView.placements.length} revealed`}
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
          ) : team && teams ? (
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
            roundLabel={`${roundLabel(roundView.group, roundView.roundNumber)} results`}
            meta={`Rules v${roundView.rulesVersion} · checksum ${roundView.payloadChecksum.slice(0, 12)}…`}
            autoPlayOnStart={false}
            layout="live"
            saveId={saveId}
            onRevealedChange={setRevealed}
          />
        ) : (
          <Card eyebrow="Postseason event" title={EVENT_TITLES[event]}>
            <p className="muted">{complete ? 'Pick a round to replay it.' : 'Press Next Round to simulate round 1.'}</p>
          </Card>
        )}
      </section>
    </div>
  );
}
