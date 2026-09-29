import { useEffect, useMemo, useState } from 'react';
import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import { ApiError, apiErrorMessage } from '../../shared/api/http';
import {
  fetchCurrentStandings,
  type CurrentStandings,
} from '../athletes/athleteApi';
import type { SeasonProgress } from '../dashboard/dashboardApi';
import { RoundReveal } from '../reveal/RoundReveal';
import { AthleteLink, Link } from '../routing/router';
import { savesPath, dashboardPath, standingsLeaguePath, standingsPath } from '../routing/routes';
import { advanceRound, completeStage, type StageRound } from './liveApi';
import { useStageRounds } from './useLiveRound';
import { colorComposition, zoneLabelForRank } from '../standings/zones';
import './LivePage.css';

const LEAGUE_KEY_PREFIX = 'mtg-solo-sports:live-league:';

/** Display-only projection of a fixed-point thousandths value (no sporting math). */
function formatPoints(thousandths: number): string {
  return (thousandths / 1000).toFixed(3);
}

function readStored(key: string): string | null {
  try {
    const value = localStorage.getItem(key);
    return value && value.length > 0 ? value : null;
  } catch {
    return null;
  }
}

function writeStored(key: string, value: string): void {
  try {
    localStorage.setItem(key, value);
  } catch {
    // storage is best-effort; selection still works in memory
  }
}

export function LivePage({
  saveId,
  progress,
  progressLoading,
  progressNotFound,
  progressError,
  onMutated,
  urlLeagueId,
  urlRound,
  onLeagueChange,
  onRoundChange,
}: {
  saveId: string;
  progress: SeasonProgress | null;
  progressLoading: boolean;
  progressNotFound: boolean;
  progressError: string | null;
  onMutated: () => void;
  /** Shareable league selection from `?league=`; null means stored/default fallback. */
  urlLeagueId: number | null;
  /** Shareable round selection from `?round=`; null means latest persisted round. */
  urlRound: number | null;
  onLeagueChange: (leagueId: number) => void;
  onRoundChange: (round: number | null) => void;
}) {
  const [leagueId, setLeagueId] = useState<number | null>(urlLeagueId);
  const [selectedRound, setSelectedRound] = useState<number | null>(urlRound);
  const [advancing, setAdvancing] = useState(false);
  const [completing, setCompleting] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);

  const leagues = useMemo(
    () => (progress ? [...progress.leagues].sort((a, b) => a.leagueId - b.leagueId) : []),
    [progress],
  );
  const league = leagues.find((entry) => entry.leagueId === leagueId) ?? null;
  const stageNumber = league?.currentStage ?? progress?.globalStage ?? null;
  const effectiveStage = league?.isLeagueComplete ? null : stageNumber;

  // Sync Back/Forward navigation of `?league=` into local selection.
  useEffect(() => {
    if (urlLeagueId !== null && urlLeagueId !== leagueId) {
      setLeagueId(urlLeagueId);
      setSelectedRound(urlRound);
    } else if (urlLeagueId === null && urlRound !== selectedRound && urlRound !== null) {
      setSelectedRound(urlRound);
    }
    // Sync only on URL changes; local edits push the URL via callbacks.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [urlLeagueId, urlRound]);

  useEffect(() => {
    if (!saveId || !progress) {
      if (!progress) {
        setLeagueId((current) => (urlLeagueId !== null ? urlLeagueId : current));
      }
      return;
    }
    // An explicit shareable `?league=` wins when it names a league in this save.
    if (urlLeagueId !== null) {
      const match = progress.leagues.find((entry) => entry.leagueId === urlLeagueId);
      if (match) {
        setLeagueId(match.leagueId);
        return;
      }
      // Invalid league query falls back below without clobbering the URL.
    }
    if (leagueId !== null && progress.leagues.some((entry) => entry.leagueId === leagueId)) {
      return;
    }
    const stored = readStored(`${LEAGUE_KEY_PREFIX}${saveId}`);
    const storedId = stored ? Number.parseInt(stored, 10) : Number.NaN;
    const match = progress.leagues.find((entry) => entry.leagueId === storedId);
    if (match) {
      setLeagueId(match.leagueId);
      return;
    }
    const first = [...progress.leagues].sort((a, b) => a.leagueId - b.leagueId)[0];
    setLeagueId(first ? first.leagueId : null);
  }, [saveId, progress, urlLeagueId, leagueId]);

  const stageRounds = useStageRounds(saveId, leagueId, effectiveStage);
  const completedRounds = stageRounds.rounds?.completedRounds ?? 0;
  const roundsPerStage = stageRounds.rounds?.roundsPerStage ?? 16;
  const isStageComplete = stageRounds.rounds?.isStageComplete ?? false;

  const [standings, setStandings] = useState<CurrentStandings | null>(null);
  const [standingsLoading, setStandingsLoading] = useState(false);
  const [standingsError, setStandingsError] = useState<string | null>(null);

  useEffect(() => {
    if (!saveId || leagueId === null) {
      setStandings(null);
      setStandingsLoading(false);
      setStandingsError(null);
      return;
    }
    const controller = new AbortController();
    const { signal } = controller;
    setStandingsLoading(true);
    setStandingsError(null);
    fetchCurrentStandings(saveId, leagueId, signal)
      .then((loaded) => {
        setStandings(loaded);
        setStandingsLoading(false);
      })
      .catch((failure: unknown) => {
        if (failure instanceof DOMException && failure.name === 'AbortError') {
          return;
        }
        if (failure instanceof ApiError && failure.status === 404) {
          setStandings(null);
          setStandingsError(null);
        } else {
          setStandingsError(apiErrorMessage(failure));
        }
        setStandingsLoading(false);
      });
    return () => {
      controller.abort();
    };
  }, [saveId, leagueId, completedRounds, isStageComplete]);

  const visibleRound: StageRound | null = useMemo(() => {
    const rounds = stageRounds.rounds?.rounds ?? [];
    if (rounds.length === 0) {
      return null;
    }
    const wanted = selectedRound ?? rounds[rounds.length - 1]!.roundNumber;
    return rounds.find((round) => round.roundNumber === wanted) ?? rounds[rounds.length - 1]!;
  }, [stageRounds.rounds, selectedRound]);

  useEffect(() => {
    if (!stageRounds.rounds) {
      return;
    }
    const total = stageRounds.rounds.completedRounds;
    if (total === 0) {
      if (selectedRound !== null) {
        setSelectedRound(null);
      }
      return;
    }
    setSelectedRound((current) => {
      // An explicit shareable `?round=` wins when it names a persisted round.
      if (urlRound !== null) {
        const exists = stageRounds.rounds?.rounds.some((row) => row.roundNumber === urlRound);
        if (exists) {
          return urlRound;
        }
      }
      if (current === null || current > total) {
        return total;
      }
      return current;
    });
  }, [stageRounds.rounds, urlRound, selectedRound]);

  if (progressNotFound) {
    return (
      <Notice tone="error" title="Save unavailable">
        <p>That save no longer exists. Pick another universe on the Saves page.</p>
        <p>
          <Link to={savesPath()} className="primary-button">
            Back to saves
          </Link>
        </p>
      </Notice>
    );
  }

  if ((progressLoading && !progress) || (stageRounds.loading && !stageRounds.rounds)) {
    return <Loading label="Loading live competition…" />;
  }

  if (!progress) {
    return (
      <Notice tone="error" title="Competition unavailable">
        <p>{progressError ?? 'Sporting progress could not be loaded for this save.'}</p>
        <p>
          <Link to={savesPath()} className="ghost-button">
            Back to saves
          </Link>
        </p>
      </Notice>
    );
  }

  const gateLegal =
    !progress.isSeasonComplete &&
    league !== null &&
    !league.isLeagueComplete &&
    league.currentStage === progress.globalStage;
  const gateReason = progress.isSeasonComplete
    ? 'The season is complete; final tables are persisted.'
    : league?.isLeagueComplete
      ? 'This league has completed its season.'
      : league && league.currentStage !== progress.globalStage
        ? `Stage ${league.currentStage} waits for the global stage (${progress.globalStage}) to catch up.`
        : null;
  const busy = advancing || completing;
  const canAdvance =
    gateLegal && !isStageComplete && completedRounds < roundsPerStage && !busy;
  const canComplete = gateLegal && !isStageComplete && !busy;
  const advanceTitle = !gateLegal
    ? (gateReason ?? 'Round advancement is not legal right now.')
    : isStageComplete || completedRounds >= roundsPerStage
      ? 'This stage already holds 16 persisted rounds; complete the stage.'
      : 'Simulate the next round on the backend and replay the persisted result.';
  const completeTitle = !gateLegal
    ? (gateReason ?? 'Stage completion is not legal right now.')
    : isStageComplete
      ? 'This stage is already complete.'
      : 'Simulate any remaining rounds on the backend and persist stage standings.';

  async function handleAdvance(): Promise<void> {
    if (!saveId || leagueId === null || !canAdvance) {
      return;
    }
    setAdvancing(true);
    setActionError(null);
    try {
      const result = await advanceRound(saveId, leagueId);
      setSelectedRound(result.roundNumber);
      onRoundChange(result.roundNumber);
      stageRounds.refresh();
      onMutated();
    } catch (failure) {
      setActionError(apiErrorMessage(failure));
    } finally {
      setAdvancing(false);
    }
  }

  async function handleCompleteStage(): Promise<void> {
    if (!saveId || leagueId === null || !canComplete) {
      return;
    }
    setCompleting(true);
    setActionError(null);
    try {
      await completeStage(saveId, leagueId);
      setSelectedRound(null);
      onRoundChange(null);
      stageRounds.refresh();
      onMutated();
    } catch (failure) {
      setActionError(apiErrorMessage(failure));
    } finally {
      setCompleting(false);
    }
  }

  const revealKey = visibleRound
    ? `${stageRounds.rounds?.stageNumber ?? effectiveStage ?? 0}:${visibleRound.roundNumber}:${visibleRound.payloadChecksum}`
    : null;
  const revealMeta = visibleRound
    ? `Round ${visibleRound.roundNumber} · rules v${visibleRound.rulesVersion} · checksum ` +
      (visibleRound.payloadChecksum.length > 12
        ? `${visibleRound.payloadChecksum.slice(0, 12)}…`
        : visibleRound.payloadChecksum)
    : undefined;

  return (
    <div className="live-layout">
      <aside className="live-sidebar" aria-label="Competition management">
        <Card
          eyebrow="Live competition"
          title={league ? `${league.leagueName} — Stage ${effectiveStage ?? '—'}` : 'Live competition'}
        >
          <div className="live-manage-controls">
            <label className="field">
              <span>League</span>
              <select
                value={leagueId ?? ''}
                disabled={leagues.length === 0 || busy}
                onChange={(event) => {
                  const next = Number.parseInt(event.target.value, 10);
                  if (Number.isNaN(next)) {
                    return;
                  }
                  setLeagueId(next);
                  setSelectedRound(null);
                  writeStored(`${LEAGUE_KEY_PREFIX}${saveId}`, String(next));
                  onLeagueChange(next);
                }}
              >
                {leagues.map((entry) => (
                  <option key={entry.leagueId} value={entry.leagueId}>
                    {entry.leagueName}
                  </option>
                ))}
              </select>
            </label>
            <div className="live-buttons">
              <button
                type="button"
                className="primary-button"
                disabled={!canAdvance}
                title={advanceTitle}
                onClick={() => {
                  void handleAdvance();
                }}
              >
                {advancing ? 'Simulating…' : 'Next Round'}
              </button>
              <button
                type="button"
                className="ghost-button"
                disabled={!canComplete}
                title={completeTitle}
                onClick={() => {
                  void handleCompleteStage();
                }}
              >
                {completing ? 'Completing…' : 'Complete Stage'}
              </button>
            </div>
          </div>
          {gateReason ? <p className="muted small">{gateReason}</p> : null}
          <p className="muted small live-count">
            Completed rounds in this stage: {completedRounds} / {roundsPerStage}
            {isStageComplete ? ' · stage complete' : ''}
          </p>
          {actionError ? (
            <Notice tone="error" title="Simulation failed">
              <p>{actionError}</p>
              <p>
                <button type="button" className="ghost-button" onClick={() => setActionError(null)}>
                  Dismiss
                </button>
              </p>
            </Notice>
          ) : null}
          <details className="live-help">
            <summary>About live replay</summary>
            <p className="muted small">
              The backend result is authoritative; refreshing only re-reads persisted rounds and
              never resimulates. The reveal replays those immutable rows with
              presentation-only animation.
            </p>
            <p className="muted small">
              Need the whole season at once?{' '}
              <Link to={dashboardPath(saveId)}>
                Fast-forward the league season on the Dashboard
              </Link>{' '}
              — one step completes every remaining league stage and stops before postseason.
            </p>
          </details>
        </Card>

        <Card
          eyebrow="Rounds"
          title={`Stage ${effectiveStage ?? '—'} · ${completedRounds}/${roundsPerStage}`}
        >
          {stageRounds.error && !stageRounds.rounds ? (
            <Notice tone="error" title="Rounds unavailable">
              <p>{stageRounds.error}</p>
              <p>
                <button type="button" className="ghost-button" onClick={stageRounds.refresh}>
                  Retry
                </button>
              </p>
            </Notice>
          ) : completedRounds === 0 ? (
            <Notice tone="empty" title="No rounds yet">
              <p>
                {canAdvance
                  ? 'Press Next Round to simulate round 1 on the backend.'
                  : 'No persisted rounds exist for this stage yet.'}
              </p>
            </Notice>
          ) : (
            <div className="round-pills round-pills-compact" role="group" aria-label="Completed rounds">
              {stageRounds.rounds?.rounds.map((round) => (
                <button
                  key={round.roundNumber}
                  type="button"
                  className={
                    (selectedRound ?? completedRounds) === round.roundNumber
                      ? 'nav-item current'
                      : 'nav-item'
                  }
                  aria-pressed={(selectedRound ?? completedRounds) === round.roundNumber}
                  disabled={busy}
                  onClick={() => {
                    setSelectedRound(round.roundNumber);
                    onRoundChange(round.roundNumber);
                  }}
                >
                  {round.roundNumber}
                </button>
              ))}
            </div>
          )}
        </Card>
      </aside>

      <div className="live-main">
        {visibleRound && revealKey ? (
          <RoundReveal
            placements={visibleRound.placements}
            revealKey={revealKey}
            roundLabel={`Round ${visibleRound.roundNumber} results`}
            meta={revealMeta}
            autoPlayOnStart={false}
            layout="live"
            saveId={saveId}
          />
        ) : stageRounds.error && !stageRounds.rounds ? (
          <Notice tone="error" title="Rounds unavailable">
            <p>{stageRounds.error}</p>
            <p>
              <button type="button" className="ghost-button" onClick={stageRounds.refresh}>
                Retry
              </button>
            </p>
          </Notice>
        ) : (
          <Notice tone="empty" title="No rounds yet">
            <p>
              {canAdvance
                ? 'Press Next Round to simulate round 1 on the backend.'
                : 'No persisted rounds exist for this stage yet.'}
            </p>
          </Notice>
        )}

      <Card
        eyebrow="Standings"
        title={
          standings
            ? `${standings.leagueName} — ${standings.completedStages} stage(s)${standings.isFinal ? ' · final' : ''}`
            : 'Current standings'
        }
        action={
          leagueId !== null ? (
            <Link
              to={standingsLeaguePath(saveId, leagueId)}
              className="ghost-button"
              title="Open the dedicated league tables and stage placements"
            >
              Full standings
            </Link>
          ) : (
            <Link
              to={standingsPath(saveId)}
              className="ghost-button"
              title="Open the dedicated league tables and stage placements"
            >
              Full standings
            </Link>
          )
        }
      >
        {standingsLoading && !standings ? (
          <Loading label="Loading standings…" />
        ) : standingsError ? (
          <Notice tone="error" title="Standings unavailable">
            <p>{standingsError}</p>
          </Notice>
        ) : !standings || standings.standings.length === 0 ? (
          <Notice tone="empty" title="No standings yet">
            <p>Complete a stage on the backend to populate championship standings.</p>
          </Notice>
        ) : (
          <>
            {(() => {
              const kind = league?.leagueKind ?? 'Feeder';
              const composition =
                kind === 'Superleague' ? colorComposition(standings.standings) : [];
              return composition.length > 0 ? (
                <>
                  <h3 className="reveal-subhead">Color composition — no quotas applied</h3>
                  <ul className="color-counts">
                    {composition.map((entry) => (
                      <li key={entry.color}>
                        <span>{entry.color}</span>
                        <strong>{entry.count}/32</strong>
                      </li>
                    ))}
                  </ul>
                </>
              ) : null;
            })()}
            <div className="table-wrap">
              <table className="data-table">
                <thead>
                  <tr>
                    <th scope="col">Rank</th>
                    <th scope="col">Card</th>
                    <th scope="col">Color</th>
                    <th scope="col">Zone</th>
                    <th scope="col">Champ pts</th>
                    <th scope="col">Stage W</th>
                    <th scope="col">Round W</th>
                  </tr>
                </thead>
                <tbody>
                  {standings.standings.map((row) => {
                    const kind = league?.leagueKind ?? 'Feeder';
                    return (
                      <tr key={row.athleteId}>
                        <td className="numeric">{row.seasonRank}</td>
                        <td>
                          <div className="card-cell">
                            {row.imageUrl ? (
                              <img className="card-thumb" src={row.imageUrl} alt="" loading="lazy" />
                            ) : (
                              <span className="card-thumb card-thumb-fallback" aria-hidden="true">
                                {row.name.slice(0, 2).toUpperCase()}
                              </span>
                            )}
                            <span className="card-identity">
                              <AthleteLink saveId={saveId} athleteId={row.athleteId} name={row.name} />
                              {row.isChampion ? <span className="card-sub"> · Champion</span> : null}
                            </span>
                          </div>
                        </td>
                        <td>{row.sportingColorName}</td>
                        <td>
                          <span className="badge badge-wait" title="Visual zone only; no quotas applied.">
                            {zoneLabelForRank(row.seasonRank, kind)}
                          </span>
                        </td>
                        <td className="numeric">{formatPoints(row.totalChampionshipPointsThousandths)}</td>
                        <td className="numeric">{row.stageWins}</td>
                        <td className="numeric">{row.roundWins}</td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          </>
        )}
        <p className="muted small">
          Standings accumulate persisted stage championship points and link each card row
          to its career profile. Zones are visual only: Superleague 1–16 safe, 17–24
          qualifier, 25–32 relegated; feeders champion auto-promoted plus 2–4 qualifier.
          No color quotas are applied. The dedicated Standings page adds the full
          season table plus the stage-by-stage placement matrix for bonus calibration.
        </p>
      </Card>
      </div>
    </div>
  );
}
