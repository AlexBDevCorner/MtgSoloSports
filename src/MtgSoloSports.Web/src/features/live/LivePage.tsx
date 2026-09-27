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
import { advanceRound, completeStage, type StageRound } from './liveApi';
import { useStageRounds } from './useLiveRound';

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
  hasSelection,
  onMutated,
  onGoToSaves,
  onSelectAthlete,
}: {
  saveId: string | null;
  progress: SeasonProgress | null;
  progressLoading: boolean;
  hasSelection: boolean;
  onMutated: () => void;
  onGoToSaves: () => void;
  onSelectAthlete: (athleteId: number) => void;
}) {
  const [leagueId, setLeagueId] = useState<number | null>(null);
  const [selectedRound, setSelectedRound] = useState<number | null>(null);
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

  useEffect(() => {
    if (!saveId || !progress) {
      setLeagueId(null);
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
  }, [saveId, progress]);

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
      setSelectedRound(null);
      return;
    }
    const total = stageRounds.rounds.completedRounds;
    if (total === 0) {
      setSelectedRound(null);
      return;
    }
    setSelectedRound((current) => {
      if (current === null || current > total) {
        return total;
      }
      return current;
    });
  }, [stageRounds.rounds]);

  if (!hasSelection || !saveId) {
    return (
      <Notice tone="empty" title="No save selected">
        <p>Pick a universe on the Saves tab to run live rounds.</p>
        <p>
          <button type="button" className="primary-button" onClick={onGoToSaves}>
            Go to saves
          </button>
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
        <p>Sporting progress could not be loaded for this save.</p>
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
      stageRounds.refresh();
      onMutated();
    } catch (failure) {
      setActionError(apiErrorMessage(failure));
    } finally {
      setCompleting(false);
    }
  }

  function openAthlete(athleteId: number): void {
    onSelectAthlete(athleteId);
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
    <div className="dashboard">
      <Card
        eyebrow="Live competition"
        title={league ? `${league.leagueName} — Stage ${effectiveStage ?? '—'}` : 'Live competition'}
      >
        <div className="live-controls">
          <label className="field">
            <span>League</span>
            <select
              value={leagueId ?? ''}
              disabled={leagues.length === 0 || busy}
              onChange={(event) => {
                const next = Number.parseInt(event.target.value, 10);
                setLeagueId(Number.isNaN(next) ? null : next);
                setSelectedRound(null);
                if (saveId && !Number.isNaN(next)) {
                  writeStored(`${LEAGUE_KEY_PREFIX}${saveId}`, String(next));
                }
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
        <p className="muted small">
          The backend result is authoritative; refreshing only re-reads persisted rounds and
          never resimulates. The reveal below replays those immutable rows with
          presentation-only animation.
        </p>
        {gateReason ? <p className="muted small">{gateReason}</p> : null}
        <p className="muted small">
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
      </Card>

      <Card eyebrow="Rounds" title={`Persisted rounds — Stage ${effectiveStage ?? '—'}`}>
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
          <div className="round-pills" role="group" aria-label="Completed rounds">
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
                }}
              >
                {round.roundNumber}
              </button>
            ))}
          </div>
        )}
      </Card>

      {visibleRound && revealKey ? (
        <RoundReveal
          placements={visibleRound.placements}
          revealKey={revealKey}
          roundLabel={`Round ${visibleRound.roundNumber} results`}
          meta={revealMeta}
          onSelectAthlete={openAthlete}
        />
      ) : null}

      <Card
        eyebrow="Standings"
        title={
          standings
            ? `${standings.leagueName} — ${standings.completedStages} stage(s)${standings.isFinal ? ' · final' : ''}`
            : 'Current standings'
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
          <div className="table-wrap">
            <table className="data-table">
              <thead>
                <tr>
                  <th scope="col">Rank</th>
                  <th scope="col">Card</th>
                  <th scope="col">Champ pts</th>
                  <th scope="col">Stage W</th>
                  <th scope="col">Round W</th>
                </tr>
              </thead>
              <tbody>
                {standings.standings.map((row) => (
                  <tr key={row.athleteId}>
                    <td className="numeric">{row.seasonRank}</td>
                    <td>
                      <button
                        type="button"
                        className="card-name card-link"
                        title={`Open career profile for ${row.name}`}
                        onClick={() => {
                          openAthlete(row.athleteId);
                        }}
                      >
                        {row.name}
                      </button>
                      {row.isChampion ? <span className="card-sub"> · Champion</span> : null}
                    </td>
                    <td className="numeric">{formatPoints(row.totalChampionshipPointsThousandths)}</td>
                    <td className="numeric">{row.stageWins}</td>
                    <td className="numeric">{row.roundWins}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        <p className="muted small">
          Standings accumulate persisted stage championship points and link each card row
          to its career profile.
        </p>
      </Card>
    </div>
  );
}
