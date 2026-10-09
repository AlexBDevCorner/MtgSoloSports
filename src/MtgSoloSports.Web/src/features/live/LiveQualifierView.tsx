import { useCallback, useEffect, useMemo, useState } from 'react';
import { apiErrorMessage } from '../../shared/api/http';
import { qualifierBoundaryLabel } from '../../shared/leagueTiers';
import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import {
  fetchQualifierRound,
  fetchQualifierRounds,
  playFeederQualifierRound,
  runFeederQualifier,
  runRemainingQualifiers,
  type QualifierRoundsDetail,
  type QualifierRoundView,
} from '../qualifiers/qualifierApi';
import {
  canonicalQualifierLiveParams,
  nextQualifierLiveParam,
  parseQualifierLiveParam,
  qualifierApiBoundary,
  qualifierApiColor,
} from '../qualifiers/qualifierModel';
import { RoundReveal } from '../reveal/RoundReveal';
import type { RevealPlacement } from '../reveal/types';
import { AthleteLink, Link } from '../routing/router';
import { dashboardPath, qualifierLivePath, qualifiersPath } from '../routing/routes';
import './LivePage.css';

/**
 * Live view of one individual feeder qualifier (MSS-069): a real 16-round
 * event with its own animated reveal, cumulative standings and top-8 cutoff.
 * Each event shows its own `Round N / 16` progress — never the phase-wide
 * `Y / 272` total under this individual title. Phase totals (`X / 17`,
 * `Y / 272`) live on the qualifier overview only.
 *
 * Backend is authoritative: Next Round persists exactly one round with the
 * versioned RNG; replay re-reads persisted rows and never resimulates.
 */
export function LiveQualifierView({
  saveId,
  qualifier,
  season,
  urlRound,
  canPlay,
  onSelectRound,
  onMutated,
}: {
  saveId: string;
  /** Live `?qualifier=` param: `f1f2-<color>` or `f2f3-<color>`. */
  qualifier: string;
  /** Source season of the postseason transition. */
  season: number;
  /** Selected persisted round from `?round=`; null follows the latest. */
  urlRound: number | null;
  /** True while `RunQualifier` is the save's legal next action for `season`. */
  canPlay: boolean;
  onSelectRound: (round: number | null) => void;
  onMutated: () => void;
}) {
  const { boundary, color } = useMemo(() => parseQualifierLiveParam(qualifier), [qualifier]);
  const apiBoundary = useMemo(() => qualifierApiBoundary(qualifier.toLowerCase()), [qualifier]);
  const apiColor = useMemo(() => qualifierApiColor(qualifier), [qualifier]);
  const [detail, setDetail] = useState<QualifierRoundsDetail | null>(null);
  const [roundView, setRoundView] = useState<QualifierRoundView | null>(null);
  const [revealed, setRevealed] = useState<readonly RevealPlacement[]>([]);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [revision, setRevision] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    fetchQualifierRounds(saveId, apiBoundary, apiColor, season, controller.signal)
      .then((loaded) => {
        setDetail(loaded);
        setLoading(false);
      })
      .catch((failure: unknown) => {
        if (!(failure instanceof DOMException && failure.name === 'AbortError')) {
          setError(apiErrorMessage(failure));
        }
        setLoading(false);
      });
    return () => controller.abort();
  }, [saveId, apiBoundary, apiColor, season, revision]);

  const selectedRoundNumber: number | null = useMemo(() => {
    const rounds = detail?.rounds ?? [];
    if (rounds.length === 0) {
      return null;
    }
    if (urlRound !== null && rounds.some((r) => r.roundNumber === urlRound)) {
      return urlRound;
    }
    return rounds[rounds.length - 1]!.roundNumber;
  }, [detail, urlRound]);

  useEffect(() => {
    if (selectedRoundNumber === null) {
      return;
    }
    if (roundView && roundView.roundNumber === selectedRoundNumber) {
      return;
    }
    const controller = new AbortController();
    fetchQualifierRound(saveId, apiBoundary, apiColor, selectedRoundNumber, season, controller.signal)
      .then(setRoundView)
      .catch((failure: unknown) => {
        if (!(failure instanceof DOMException && failure.name === 'AbortError')) {
          setError(apiErrorMessage(failure));
        }
      });
    return () => controller.abort();
    // roundView is the cache this effect fills; re-running on it would loop.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [saveId, apiBoundary, apiColor, season, selectedRoundNumber]);

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
      const result = await playFeederQualifierRound(saveId, apiBoundary, apiColor);
      setRoundView(result.round);
      onSelectRound(result.round.roundNumber);
      refreshAfter();
    } catch (failure) {
      setError(apiErrorMessage(failure));
    } finally {
      setBusy(false);
    }
  }

  async function handleRunThis(): Promise<void> {
    if (busy) {
      return;
    }
    setBusy(true);
    setError(null);
    try {
      await runFeederQualifier(saveId, apiBoundary, apiColor);
      setRoundView(null);
      onSelectRound(null);
      refreshAfter();
    } catch (failure) {
      setError(apiErrorMessage(failure));
    } finally {
      setBusy(false);
    }
  }

  async function handleRunAll(): Promise<void> {
    if (busy) {
      return;
    }
    setBusy(true);
    setError(null);
    try {
      await runRemainingQualifiers(saveId);
      setRoundView(null);
      onSelectRound(null);
      refreshAfter();
    } catch (failure) {
      setError(apiErrorMessage(failure));
    } finally {
      setBusy(false);
    }
  }

  const roundsPlayed = detail?.roundsPlayed ?? 0;
  const totalRounds = detail?.totalRounds ?? 16;
  const complete = detail?.isComplete ?? false;
  const title = `${qualifierBoundaryLabel(boundary, color)} · Season ${season}`;
  const progressText = `Round ${roundsPlayed} / ${totalRounds}`;
  const nextParam = nextQualifierLiveParam(qualifier.toLowerCase());
  const order = canonicalQualifierLiveParams();
  const position = order.indexOf(qualifier.toLowerCase());

  const incumbents = useMemo(
    () => (detail?.field ?? []).filter((m) => m.role === 'Incumbent'),
    [detail],
  );
  const challengers = useMemo(
    () => (detail?.field ?? []).filter((m) => m.role === 'Challenger'),
    [detail],
  );
  const pills = detail?.rounds ?? [];

  return (
    <div className="live-layout">
      <aside className="live-sidebar" aria-label="Qualifier management">
        <Card eyebrow="Qualifier on Live" title={title}>
          <p className="muted small live-count">{progressText}</p>
          <p className="muted small">
            {boundary === 'Feeder1Feeder2'
              ? 'Feeder 1 places 17–24 defend against Feeder 2 places 9–16: 16 athletes, 16 rounds, top 8 start next season in Feeder 1.'
              : 'Feeder 2 places 17–24 defend against Feeder 3 places 9–16: 16 athletes, 16 rounds, top 8 start next season in Feeder 2.'}{' '}
            {incumbents.length === 8 && challengers.length === 8
              ? `Field: 8 incumbents vs 8 challengers (${color}).`
              : null}{' '}
            Top-8 cutoff decides who takes the higher tier.
          </p>
          {complete ? (
            <Notice tone="info" title={`${qualifierBoundaryLabel(boundary, color)} complete`}>
              <p className="live-buttons">
                {nextParam ? (
                  <Link
                    to={qualifierLivePath(saveId, nextParam, season, null)}
                    className="primary-button"
                  >
                    Play next qualifier on Live
                  </Link>
                ) : (
                  <Link to={dashboardPath(saveId)} className="primary-button">
                    Continue on the Dashboard
                  </Link>
                )}
                <Link to={qualifiersPath(saveId, { season })} className="ghost-button">
                  All 17 qualifiers
                </Link>
              </p>
              {position >= 0 ? (
                <p className="muted small">
                  Qualifier {position + 1} of 17 in canonical order — Superleague first, then
                  Feeder 1↔Feeder 2 by color, then Feeder 2↔Feeder 3 by color.
                </p>
              ) : null}
            </Notice>
          ) : canPlay ? (
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
                  title="Persists every remaining round of this qualifier now (resume-safe)."
                  onClick={() => {
                    void handleRunThis();
                  }}
                >
                  Run this qualifier
                </button>
                <button
                  type="button"
                  className="ghost-button"
                  disabled={busy}
                  title="Persists every remaining qualifier in canonical order (resume-safe)."
                  onClick={() => {
                    void handleRunAll();
                  }}
                >
                  Run all remaining qualifiers
                </button>
              </div>
              <p className="muted small">{"Each round is saved and can't be undone."}</p>
              {nextParam ? (
                <p className="muted small">
                  After this event, continue directly to the next qualifier on Live — no
                  Dashboard trip needed.{' '}
                  <Link to={qualifierLivePath(saveId, nextParam, season, null)}>
                    Next qualifier
                  </Link>
                  .
                </p>
              ) : null}
            </div>
          ) : (
            <Notice tone="info" title="Not the next qualifier">
              <p>
                This qualifier can be replayed once it is resolved, or played once it is the
                next pending event in canonical order.
              </p>
              <p className="live-buttons">
                <Link to={qualifiersPath(saveId, { season })} className="primary-button">
                  All 17 qualifiers
                </Link>
                <Link to={dashboardPath(saveId)} className="ghost-button">
                  Dashboard
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
        </Card>

        <Card eyebrow="Rounds" title={`Played rounds · ${roundsPlayed}/${totalRounds}`}>
          {pills.length === 0 ? (
            <p className="muted small">No rounds played yet.</p>
          ) : (
            <div className="round-pills round-pills-compact" role="group" aria-label="Played rounds">
              {pills.map((r) => (
                <button
                  key={r.roundNumber}
                  type="button"
                  className={selectedRoundNumber === r.roundNumber ? 'nav-item current' : 'nav-item'}
                  aria-pressed={selectedRoundNumber === r.roundNumber}
                  onClick={() => onSelectRound(r.roundNumber)}
                >
                  {r.roundNumber}
                </button>
              ))}
            </div>
          )}
          {detail?.isComplete && detail.standings.length > 0 ? (
            <table className="mini-table">
              <caption className="reveal-subhead">
                Final standings — top 8 qualify
                <span className="muted small live-team-caption">
                  Cutoff after rank 8 (text badges, never color alone)
                </span>
              </caption>
              <thead>
                <tr>
                  <th scope="col" className="numeric">
                    Rank
                  </th>
                  <th scope="col">Card</th>
                  <th scope="col">Outcome</th>
                </tr>
              </thead>
              <tbody>
                {[...detail.standings]
                  .sort((a, b) => a.qualifierRank - b.qualifierRank)
                  .map((row, index) => (
                    <>
                      {index === 8 ? (
                        <tr key="cutoff" aria-hidden="false">
                          <td colSpan={3} className="muted small">
                            — Cutoff: top 8 qualify —
                          </td>
                        </tr>
                      ) : null}
                      <tr key={row.athleteId}>
                        <td className="numeric">{row.qualifierRank}</td>
                        <td>
                          <AthleteLink saveId={saveId} athleteId={row.athleteId} name={row.name} />
                          <span className="card-sub">
                            {' '}
                            · {row.role} · {row.fromLeagueName} P{row.fromSeasonRank}
                          </span>
                        </td>
                        <td>
                          <span className={row.isQualified ? 'badge badge-done' : 'badge badge-wait'}>
                            {row.isQualified ? 'QUALIFIED' : 'ELIMINATED'}
                          </span>
                        </td>
                      </tr>
                    </>
                  ))}
              </tbody>
            </table>
          ) : detail && detail.field.length > 0 ? (
            <table className="mini-table">
              <caption className="reveal-subhead">
                Field — 8 incumbents vs 8 challengers
                <span className="muted small live-team-caption">
                  Top 8 will qualify (text roles, never color alone)
                </span>
              </caption>
              <thead>
                <tr>
                  <th scope="col">Card</th>
                  <th scope="col">Role</th>
                </tr>
              </thead>
              <tbody>
                {detail.field.map((row) => (
                  <tr key={row.athleteId}>
                    <td>
                      <AthleteLink saveId={saveId} athleteId={row.athleteId} name={row.name} />
                      <span className="card-sub">
                        {' '}
                        · {row.fromLeagueName} P{row.fromSeasonRank}
                      </span>
                    </td>
                    <td>
                      <span className="badge badge-wait">{row.role}</span>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          ) : null}
        </Card>
      </aside>

      <section className="live-main" aria-label="Qualifier round">
        {loading && !roundView ? (
          <Loading label="Loading qualifier…" />
        ) : roundView ? (
          <RoundReveal
            placements={roundView.placements}
            revealKey={`feeder-qualifier:${season}:${qualifier}:${roundView.roundNumber}:${roundView.payloadChecksum}`}
            roundLabel={`Round ${roundView.roundNumber} results`}
            meta={`Rules v${roundView.rulesVersion} · checksum ${roundView.payloadChecksum.slice(0, 12)}…`}
            autoPlayOnStart={false}
            layout="live"
            saveId={saveId}
            onRevealedChange={setRevealed}
          />
        ) : (
          <Card eyebrow="Qualifier on Live" title={qualifierBoundaryLabel(boundary, color)}>
            <p className="muted">
              {complete ? 'Pick a round to replay it.' : 'Press Next Round to simulate round 1.'}
            </p>
            <p className="muted small">
              {revealed.length > 0 ? `${revealed.length} cards revealed.` : null} Cumulative score
              is {roundsPlayed > 0 ? `after Round ${roundsPlayed}` : 'at 0'}; the reveal adds each
              card&apos;s persisted final points on top.
            </p>
          </Card>
        )}
      </section>
    </div>
  );
}
