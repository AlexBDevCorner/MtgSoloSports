import { useEffect, useMemo, useState } from 'react';
import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import { apiErrorMessage } from '../../shared/api/http';
import type { SeasonProgress } from '../dashboard/dashboardApi';
import { advanceRound, completeStage, type StageRound } from './liveApi';
import { useStageRounds } from './useLiveRound';

type LiveMode = 'instant' | 'reveal';

const LEAGUE_KEY_PREFIX = 'mtg-solo-sports:live-league:';
const MODE_KEY = 'mtg-solo-sports:live-mode';

/** Display-only projection of a fixed-point thousandths value (no sporting math). */
function formatPoints(thousandths: number): string {
  return (thousandths / 1000).toFixed(3);
}

/** Display-only projection of a fixed-point bonus (no sporting math). */
function formatBonus(thousandths: number): string {
  const sign = thousandths >= 0 ? '+' : '';
  return `${sign}${(thousandths / 1000).toFixed(3)}`;
}

function formatMovement(movement: number): string {
  if (movement > 0) {
    return `+${movement}`;
  }
  return `${movement}`;
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

function cardCaption(setCode: string | null, typeLine: string): string | null {
  const parts = [setCode, typeLine].filter(
    (part): part is string => typeof part === 'string' && part.length > 0,
  );
  return parts.length > 0 ? parts.join(' · ') : null;
}

export function LivePage({
  saveId,
  progress,
  progressLoading,
  hasSelection,
  onMutated,
  onGoToSaves,
}: {
  saveId: string | null;
  progress: SeasonProgress | null;
  progressLoading: boolean;
  hasSelection: boolean;
  onMutated: () => void;
  onGoToSaves: () => void;
}) {
  const [leagueId, setLeagueId] = useState<number | null>(null);
  const [mode, setMode] = useState<LiveMode>(() =>
    readStored(MODE_KEY) === 'reveal' ? 'reveal' : 'instant',
  );
  const [selectedRound, setSelectedRound] = useState<number | null>(null);
  const [revealed, setRevealed] = useState(0);
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

  useEffect(() => {
    setRevealed(0);
  }, [visibleRound?.roundNumber, visibleRound?.payloadChecksum, mode]);

  useEffect(() => {
    if (mode !== 'reveal' || !visibleRound) {
      return;
    }
    const total = visibleRound.placements.length;
    if (revealed >= total) {
      return;
    }
    const timer = window.setTimeout(() => {
      setRevealed((value) => Math.min(value + 1, total));
    }, 120);
    return () => {
      window.clearTimeout(timer);
    };
  }, [mode, visibleRound, revealed]);

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

  const shownPlacements = visibleRound
    ? mode === 'instant'
      ? visibleRound.placements
      : visibleRound.placements.slice(0, revealed)
    : [];
  const revealTotal = visibleRound?.placements.length ?? 0;

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

  return (
    <div className="dashboard">
      <Card
        eyebrow="Live competition"
        title={league ? `${league.leagueName} — Stage ${effectiveStage ?? '—'}` : 'Live competition'}
        action={
          <div className="mode-toggle" role="group" aria-label="Results mode">
            <button
              type="button"
              className={mode === 'instant' ? 'nav-item current' : 'nav-item'}
              aria-pressed={mode === 'instant'}
              onClick={() => {
                setMode('instant');
                writeStored(MODE_KEY, 'instant');
              }}
            >
              Instant
            </button>
            <button
              type="button"
              className={mode === 'reveal' ? 'nav-item current' : 'nav-item'}
              aria-pressed={mode === 'reveal'}
              onClick={() => {
                setMode('reveal');
                writeStored(MODE_KEY, 'reveal');
              }}
            >
              Reveal
            </button>
          </div>
        }
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
          {mode === 'instant'
            ? 'Instant results render the full persisted table immediately with no client-side sporting calculations.'
            : 'Reveal mode steps through the same persisted rows as presentation-only highlighting with no client-side sporting calculations.'}{' '}
          The backend result is authoritative; refreshing only re-reads persisted rounds and
          never resimulates.
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
          <>
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
            {visibleRound ? (
              <p className="muted small">
                Round {visibleRound.roundNumber} · rules v{visibleRound.rulesVersion} · checksum{' '}
                <code title={visibleRound.payloadChecksum}>
                  {visibleRound.payloadChecksum.length > 12
                    ? `${visibleRound.payloadChecksum.slice(0, 12)}…`
                    : visibleRound.payloadChecksum}
                </code>
              </p>
            ) : null}
          </>
        )}
      </Card>

      {visibleRound ? (
        <Card
          eyebrow={mode === 'instant' ? 'Instant results' : 'Step reveal'}
          title={`Round ${visibleRound.roundNumber} results — ${visibleRound.placements.length} athletes`}
          action={
            mode === 'reveal' && revealed < revealTotal ? (
              <button
                type="button"
                className="ghost-button"
                onClick={() => {
                  setRevealed(revealTotal);
                }}
              >
                Show all ({revealed}/{revealTotal})
              </button>
            ) : undefined
          }
        >
          <div className="table-wrap">
            <table className="data-table">
              <thead>
                <tr>
                  <th scope="col">Pos</th>
                  <th scope="col">Card</th>
                  <th scope="col">Base</th>
                  <th scope="col">Bonus</th>
                  <th scope="col">Final</th>
                  <th scope="col">Rank</th>
                  <th scope="col">Stage score</th>
                </tr>
              </thead>
              <tbody>
                {shownPlacements.map((placement) => {
                  const caption = cardCaption(placement.setCode, placement.typeLine);
                  return (
                    <tr key={placement.athleteId}>
                      <td className="numeric">{placement.position}</td>
                      <td>
                        <div className="card-cell">
                          {placement.imageUrl ? (
                            <img
                              className="card-thumb"
                              src={placement.imageUrl}
                              alt=""
                              loading="lazy"
                            />
                          ) : (
                            <span className="card-thumb card-thumb-fallback" aria-hidden="true">
                              {placement.name.slice(0, 2).toUpperCase()}
                            </span>
                          )}
                          <span className="card-identity">
                            <span className="card-name">{placement.name}</span>
                            {caption ? <span className="card-sub">{caption}</span> : null}
                          </span>
                        </div>
                      </td>
                      <td className="numeric">{formatPoints(placement.baseThousandths)}</td>
                      <td className="numeric">{formatBonus(placement.activeBonusThousandths)}</td>
                      <td className="numeric">{formatPoints(placement.finalThousandths)}</td>
                      <td className="numeric">
                        {placement.rankBefore} → {placement.rankAfter}{' '}
                        <span
                          className={
                            placement.rankMovement > 0
                              ? 'move-up'
                              : placement.rankMovement < 0
                                ? 'move-down'
                                : 'move-flat'
                          }
                        >
                          ({formatMovement(placement.rankMovement)})
                        </span>
                      </td>
                      <td className="numeric">
                        {formatPoints(placement.cumulativeAfterThousandths)}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
          {mode === 'reveal' && revealed < revealTotal ? (
            <p className="muted small" aria-live="polite">
              Revealing persisted rows {revealed}/{revealTotal} — presentation only, nothing is
              recalculated.
            </p>
          ) : null}
        </Card>
      ) : null}
    </div>
  );
}
