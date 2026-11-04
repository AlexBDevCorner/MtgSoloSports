import { useEffect, useRef, useState } from 'react';
import { ApiError, apiErrorMessage } from '../../shared/api/http';
import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import { cupEditionReportUrl, downloadReport } from '../reports/reportApi';
import { AthleteLink, Link } from '../routing/router';
import { chaosLivePath, cupEditionPath, cupsPath } from '../routing/routes';
import {
  chaosCutLabel,
  chaosStageLabel,
  chaosStageShortLabel,
  formatChaosPoints,
  isFinalChaosStage,
} from '../live/chaosCupLiveModel';
import {
  fetchChaosEdition,
  fetchChaosGroup,
  fetchChaosSurvival,
  type ChaosCupEdition,
  type ChaosCupGroup,
  type ChaosCupSurvival,
} from './chaosCupApi';
import { cupTitle, formatPoints, medalBadge, stateLabel } from './cupFormat';
import { optional, type CupEdition } from './cupHistoryApi';
import { fetchCupEditions } from './cupHistoryApi';
import { cupKindOf } from './cupFormat';
import { CupPodium } from './CupPodium';
import { CardArt } from './SquadTiles';
import {
  chaosDrawRuleLabel,
  chaosGroupNumbers,
  chaosStageStrip,
  isFinalOfficial,
  survivalStageFor,
} from './chaosEditionModel';
import { ChaosRecordsPanel } from './ChaosRecordsPanel';
import './CupHistory.css';

/**
 * Compact read-only Chaos Cup season page (MSS-112). Final standings plus
 * meaningful stage summaries over persisted results only: a 1024→…→32 stage
 * strip, per-stage group listings that link to the persisted replay, stage
 * color/division survival counts from the backend, and the official Final
 * P1..P32 with podium. Detailed group standings load on demand per group so
 * the page never builds a 1024-row table; partial editions never show a
 * premature champion. No simulation, no RNG. Dossier downloads (MSS-114) read
 * the persisted backend report; they never scrape React.
 */
export function ChaosCupEditionPage({ saveId, season }: { saveId: string; season: number }) {
  const [edition, setEdition] = useState<ChaosCupEdition | null>(null);
  const [listing, setListing] = useState<CupEdition | null>(null);
  const [chaosSeasons, setChaosSeasons] = useState<number[]>([]);
  const [survival, setSurvival] = useState<ChaosCupSurvival | null>(null);
  const [finalGroup, setFinalGroup] = useState<ChaosCupGroup | null>(null);
  const [finalError, setFinalError] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [missing, setMissing] = useState(false);
  const [openStages, setOpenStages] = useState<ReadonlySet<number>>(() => new Set());
  const [openSurvival, setOpenSurvival] = useState<ReadonlySet<number>>(() => new Set());
  const [downloading, setDownloading] = useState<'json' | 'md' | null>(null);
  const [downloadError, setDownloadError] = useState<string | null>(null);

  async function handleDownload(format: 'json' | 'md'): Promise<void> {
    if (downloading !== null) {
      return;
    }
    setDownloading(format);
    setDownloadError(null);
    try {
      await downloadReport(
        cupEditionReportUrl(saveId, 'chaos', season, format),
        `cup-chaos-season-${season}-edition.${format}`,
      );
    } catch (failure) {
      setDownloadError(apiErrorMessage(failure));
    } finally {
      setDownloading(null);
    }
  }

  useEffect(() => {
    const controller = new AbortController();
    setEdition(null);
    setListing(null);
    setSurvival(null);
    setFinalGroup(null);
    setFinalError(null);
    setError(null);
    setMissing(false);
    setOpenStages(new Set());
    setOpenSurvival(new Set());
    Promise.all([fetchChaosEdition(saveId, season, controller.signal), fetchCupEditions(saveId, controller.signal)])
      .then(([chaos, cups]) => {
        if (controller.signal.aborted) {
          return;
        }
        setEdition(chaos);
        const chaosEditions = cups.editions.filter((entry) => cupKindOf(entry.cup) === 'chaos');
        setChaosSeasons(chaosEditions.map((entry) => entry.sourceSeasonNumber).sort((a, b) => a - b));
        setListing(chaosEditions.find((entry) => entry.sourceSeasonNumber === season) ?? null);
        void optional(fetchChaosSurvival(saveId, season, controller.signal))
          .then((loaded) => {
            if (!controller.signal.aborted) {
              setSurvival(loaded);
            }
          })
          .catch(() => undefined);
        if (chaos.isComplete) {
          fetchChaosGroup(saveId, 5, 1, season, controller.signal)
            .then((group) => {
              if (!controller.signal.aborted) {
                setFinalGroup(group);
              }
            })
            .catch((failure: unknown) => {
              if (failure instanceof DOMException && failure.name === 'AbortError') {
                return;
              }
              if (!controller.signal.aborted) {
                setFinalError(apiErrorMessage(failure));
              }
            });
        }
      })
      .catch((failure: unknown) => {
        if (failure instanceof DOMException && failure.name === 'AbortError') {
          return;
        }
        if (failure instanceof ApiError && failure.status === 404) {
          setMissing(true);
          return;
        }
        setError(apiErrorMessage(failure));
      });
    return () => controller.abort();
  }, [saveId, season]);

  if (missing) {
    return (
      <Notice tone="warn" title="This Cup has not been played">
        <p>
          There is no {cupTitle('chaos')} for Season {season} in this save.
        </p>
        <p>
          <Link to={cupsPath(saveId)} className="ghost-button">
            All Cups
          </Link>
        </p>
      </Notice>
    );
  }
  if (error) {
    return (
      <Notice tone="error" title="Chaos Cup unavailable">
        <p>{error}</p>
      </Notice>
    );
  }
  if (!edition) {
    return <Loading label="Loading Chaos Cup…" />;
  }

  const previous = chaosSeasons.filter((value) => value < season).at(-1) ?? null;
  const next = chaosSeasons.find((value) => value > season) ?? null;
  const officialFinal = isFinalOfficial(edition, finalGroup);
  const champion = officialFinal ? (listing?.individualChampion ?? null) : null;
  const podium = officialFinal ? (listing?.podium ?? []) : [];

  function toggle(setter: (next: ReadonlySet<number>) => void, current: ReadonlySet<number>, stage: number): void {
    setter(
      (() => {
        const nextSet = new Set(current);
        if (nextSet.has(stage)) {
          nextSet.delete(stage);
        } else {
          nextSet.add(stage);
        }
        return nextSet;
      })(),
    );
  }

  return (
    <div className="dashboard">
      <header className="cup-banner">
        <div className="cup-banner-head">
          <h1 className="cup-banner-title">Chaos Cup · Season {season}</h1>
          <nav className="cup-nav" aria-label="Cup editions">
            {previous !== null ? (
              <Link to={cupEditionPath(saveId, 'chaos', previous)} className="ghost-button">
                ← Season {previous}
              </Link>
            ) : null}
            <Link to={cupsPath(saveId)} className="ghost-button">
              All Cups
            </Link>
            {next !== null ? (
              <Link to={cupEditionPath(saveId, 'chaos', next)} className="ghost-button">
                Season {next} →
              </Link>
            ) : null}
            <button
              type="button"
              className="ghost-button"
              disabled={downloading !== null}
              onClick={() => void handleDownload('md')}
            >
              {downloading === 'md' ? 'Preparing…' : 'Report .md'}
            </button>
            <button
              type="button"
              className="ghost-button"
              disabled={downloading !== null}
              onClick={() => void handleDownload('json')}
            >
              {downloading === 'json' ? 'Preparing…' : 'Report .json'}
            </button>
          </nav>
          {downloadError ? (
            <p className="muted" role="alert">
              Download failed: {downloadError}
            </p>
          ) : null}
        </div>
        <p className="muted">
          {stateLabel(edition.state)} · {edition.fieldSize} athletes · {edition.roundsPlayed}/{edition.totalRounds}{' '}
          rounds · {edition.groupsCompleted}/{edition.totalGroups} groups
        </p>
        <p className="muted small">
          1024 entrants (league athletes plus Pool invitations) in six 32-athlete group stages; top 16 of each
          nonfinal group advance, 16 game rounds per group, active bonus applies but nothing new is earned, group
          scores reset and no league points or promotion change.
        </p>
        <p className="muted small">
          First draw: exactly 4 per sporting color in every group. Later draws: pure random, no color or tier
          seeding.
        </p>
        {champion ? (
          <p>
            Champion: <AthleteLink saveId={saveId} athleteId={champion.athleteId} name={champion.name} />
          </p>
        ) : (
          <p className="muted">The Final has not finished yet; no official champion.</p>
        )}
        <p>
          <Link to={chaosLivePath(saveId, season, null, null)} className="primary-button">
            {edition.isComplete ? 'Replay on Live' : 'Continue on Live'}
          </Link>
        </p>
      </header>

      <Card
        eyebrow="Tournament strip"
        title={chaosStageStrip()}
        info={
          <p>
            Six group stages in play order: 32, 16, 8, 4, 2 and 1 group of 32 athletes. The Final holds the
            official places 1..32; nonfinalists carry only an exit-stage category and no global rank.
          </p>
        }
      >
        <div className="table-wrap">
          <table className="data-table">
            <thead>
              <tr>
                <th scope="col">Stage</th>
                <th scope="col">Format</th>
                <th scope="col">State</th>
                <th scope="col" className="numeric">Groups done</th>
                <th scope="col" className="numeric">Rounds</th>
              </tr>
            </thead>
            <tbody>
              {edition.stages.map((stage) => (
                <tr key={stage.stageIndex}>
                  <td>{chaosStageShortLabel(stage.stageIndex)}</td>
                  <td>{chaosStageLabel(stage.stageIndex)}</td>
                  <td>{stage.isDrawn ? stateLabel(stage.state) : 'Not drawn'}</td>
                  <td className="numeric">
                    {stage.groupsCompleted}/{stage.groupCount}
                  </td>
                  <td className="numeric">
                    {stage.roundsPlayed}/{stage.totalRounds}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Card>

      {officialFinal && finalGroup ? (
        <div className="page-grid">
          <Card eyebrow="Final" title="Official podium — top 3 of 32">
            <CupPodium
              entries={podium.slice(0, 3).map((row) => ({
                key: row.teamKey,
                place: row.teamRank,
                art: <FinalArt saveId={saveId} group={finalGroup} athleteKey={row.teamKey} />,
                label: <FinalLabel saveId={saveId} group={finalGroup} athleteKey={row.teamKey} fallback={row.teamName} />,
                detail: formatPoints(row.teamScoreThousandths),
              }))}
            />
          </Card>
          <Card
            eyebrow="Final"
            title="Official Final — P1..P32"
            info={
              <p>
                The single 32-athlete Final decides places 1..32 exactly once. Every row links to the athlete
                profile; the group replays round by round on Live.
              </p>
            }
            action={
              <Link to={chaosLivePath(saveId, season, 5, 1)} className="ghost-button">
                Replay Final
              </Link>
            }
          >
            {finalError ? (
              <p className="muted" role="alert">
                {finalError}
              </p>
            ) : (
              <div className="table-wrap">
                <table className="data-table">
                  <thead>
                    <tr>
                      <th scope="col" className="numeric">Place</th>
                      <th scope="col">Card</th>
                      <th scope="col" className="numeric">Final score</th>
                      <th scope="col" className="numeric">Round W</th>
                      <th scope="col">Medal</th>
                    </tr>
                  </thead>
                  <tbody>
                    {[...finalGroup.standings]
                      .sort((a, b) => a.groupRank - b.groupRank)
                      .map((row) => (
                        <tr key={row.saveAthleteId}>
                          <td className="numeric">{row.groupRank}</td>
                          <td>
                            <AthleteLink saveId={saveId} athleteId={row.saveAthleteId} name={row.name} />
                          </td>
                          <td className="numeric">{formatChaosPoints(row.groupScoreThousandths)}</td>
                          <td className="numeric">{row.roundWins}</td>
                          <td>{medalBadge(row.groupRank === 1 ? 'Gold' : row.groupRank === 2 ? 'Silver' : row.groupRank === 3 ? 'Bronze' : null)}</td>
                        </tr>
                      ))}
                  </tbody>
                </table>
              </div>
            )}
          </Card>
        </div>
      ) : edition.isComplete ? (
        <Card eyebrow="Final" title="Official Final">
          {finalError ? (
            <Notice tone="error" title="Final unavailable">
              <p>{finalError}</p>
            </Notice>
          ) : (
            <Loading label="Loading official Final…" />
          )}
        </Card>
      ) : null}

      <section className="page-section" aria-labelledby="chaos-records">
        <h2 id="chaos-records" className="section-title">
          Chaos records &amp; Tournament Stories
        </h2>
        <ChaosRecordsPanel saveId={saveId} />
      </section>

      <section className="page-section" aria-labelledby="chaos-stages">
        <h2 id="chaos-stages" className="section-title">
          Stages &amp; groups
        </h2>
        <div className="page-grid">
          {edition.stages.map((stage) => {
            const groupsOpen = openStages.has(stage.stageIndex);
            const survivalOpen = openSurvival.has(stage.stageIndex);
            const survivalStage = survivalStageFor(survival, stage.stageIndex);
            return (
              <Card
                key={stage.stageIndex}
                eyebrow={chaosStageLabel(stage.stageIndex)}
                title={`${stage.entrantCount} athletes · ${stage.groupCount} group${stage.groupCount === 1 ? '' : 's'}`}
                info={<p>{chaosDrawRuleLabel(stage.stageIndex)}</p>}
              >
                <p className="muted small">
                  {stage.isDrawn ? stateLabel(stage.state) : 'Not drawn'} · {stage.groupsCompleted}/{stage.groupCount}{' '}
                  groups · {stage.roundsPlayed}/{stage.totalRounds} rounds
                  {survivalStage
                    ? stage.isFinal
                      ? ` · ${survivalStage.finalistCount} finalists`
                      : ` · ${survivalStage.advancedCount} advanced · ${survivalStage.eliminatedCount} eliminated · ${survivalStage.pendingCount} pending`
                    : null}
                  {!stage.isComplete && stage.isDrawn ? ' · provisional' : ''}
                </p>
                {stage.isDrawn ? (
                  <>
                    <p>
                      <button
                        type="button"
                        className="ghost-button"
                        aria-expanded={groupsOpen}
                        onClick={() => toggle(setOpenStages, openStages, stage.stageIndex)}
                      >
                        {groupsOpen ? 'Hide groups' : `Show groups (${stage.groupCount})`}
                      </button>{' '}
                      <button
                        type="button"
                        className="ghost-button"
                        aria-expanded={survivalOpen}
                        onClick={() => toggle(setOpenSurvival, openSurvival, stage.stageIndex)}
                      >
                        {survivalOpen ? 'Hide color & division survival' : 'Show color & division survival'}
                      </button>
                    </p>
                    {groupsOpen ? (
                      <ul className="edition-card-podium">
                        {chaosGroupNumbers(stage.stageIndex).map((groupNumber) => (
                          <li key={groupNumber}>
                            <Link to={chaosLivePath(saveId, season, stage.stageIndex, groupNumber)}>
                              {stage.isFinal ? 'Final' : `Group ${groupNumber}`} · replay
                            </Link>{' '}
                            <GroupDetailToggle
                              saveId={saveId}
                              season={season}
                              stageIndex={stage.stageIndex}
                              groupNumber={groupNumber}
                            />
                          </li>
                        ))}
                      </ul>
                    ) : null}
                    {survivalOpen ? (
                      <SurvivalTables stageIndex={stage.stageIndex} survival={survivalStage} />
                    ) : null}
                  </>
                ) : (
                  <p className="muted small">
                    {stage.stageIndex === 0
                      ? 'The field is not drawn yet.'
                      : 'Draws once every group of the previous stage completes.'}
                  </p>
                )}
              </Card>
            );
          })}
        </div>
      </section>
    </div>
  );
}

function FinalArt({
  saveId: _saveId,
  group,
  athleteKey,
}: {
  saveId: string;
  group: ChaosCupGroup;
  athleteKey: string;
}) {
  void _saveId;
  const row = group.standings.find((entry) => String(entry.saveAthleteId) === athleteKey);
  return <CardArt imageUrl={null} name={row?.name ?? athleteKey} />;
}

function FinalLabel({
  saveId,
  group,
  athleteKey,
  fallback,
}: {
  saveId: string;
  group: ChaosCupGroup;
  athleteKey: string;
  fallback: string;
}) {
  const row = group.standings.find((entry) => String(entry.saveAthleteId) === athleteKey);
  if (!row) {
    return <span>{fallback}</span>;
  }
  return <AthleteLink saveId={saveId} athleteId={row.saveAthleteId} name={row.name} />;
}

function SurvivalTables({
  stageIndex,
  survival,
}: {
  stageIndex: number;
  survival: ChaosCupSurvival['stages'][number] | null;
}) {
  if (!survival) {
    return <p className="muted small">Survival analytics are unavailable for this stage.</p>;
  }
  return (
    <div className="table-wrap">
      <h3 className="reveal-subhead">
        {isFinalChaosStage(stageIndex) ? 'Finalists by color and division' : 'Survival by color and division'}
        {survival.isProvisional ? ' · provisional' : ''}
      </h3>
      <table className="data-table" aria-label="Survival by sporting color">
        <thead>
          <tr>
            <th scope="col">Color</th>
            <th scope="col" className="numeric">Initial</th>
            {survival.isFinal ? (
              <th scope="col" className="numeric">Finalists</th>
            ) : (
              <>
                <th scope="col" className="numeric">Advanced</th>
                <th scope="col" className="numeric">Eliminated</th>
                <th scope="col" className="numeric">Pending</th>
              </>
            )}
          </tr>
        </thead>
        <tbody>
          {survival.byColor.map((bucket) => (
            <tr key={bucket.key}>
              <td>{bucket.label}</td>
              <td className="numeric">{bucket.initial}</td>
              {survival.isFinal ? (
                <td className="numeric">{bucket.finalists}</td>
              ) : (
                <>
                  <td className="numeric">{bucket.advanced}</td>
                  <td className="numeric">{bucket.eliminated}</td>
                  <td className="numeric">{bucket.pending}</td>
                </>
              )}
            </tr>
          ))}
        </tbody>
      </table>
      <table className="data-table" aria-label="Survival by division">
        <thead>
          <tr>
            <th scope="col">Division</th>
            <th scope="col" className="numeric">Initial</th>
            {survival.isFinal ? (
              <th scope="col" className="numeric">Finalists</th>
            ) : (
              <>
                <th scope="col" className="numeric">Advanced</th>
                <th scope="col" className="numeric">Eliminated</th>
                <th scope="col" className="numeric">Pending</th>
              </>
            )}
          </tr>
        </thead>
        <tbody>
          {survival.byDivision.map((bucket) => (
            <tr key={bucket.key}>
              <td>{bucket.label}</td>
              <td className="numeric">{bucket.initial}</td>
              {survival.isFinal ? (
                <td className="numeric">{bucket.finalists}</td>
              ) : (
                <>
                  <td className="numeric">{bucket.advanced}</td>
                  <td className="numeric">{bucket.eliminated}</td>
                  <td className="numeric">{bucket.pending}</td>
                </>
              )}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function GroupDetailToggle({
  saveId,
  season,
  stageIndex,
  groupNumber,
}: {
  saveId: string;
  season: number;
  stageIndex: number;
  groupNumber: number;
}) {
  const [open, setOpen] = useState(false);
  const [group, setGroup] = useState<ChaosCupGroup | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const requestRef = useRef(0);
  const currentGroup =
    group !== null &&
    group.saveId === saveId &&
    group.sourceSeasonNumber === season &&
    group.stageIndex === stageIndex &&
    group.groupNumber === groupNumber
      ? group
      : null;

  useEffect(() => {
    if (!open) {
      return;
    }
    if (currentGroup !== null) {
      return;
    }
    const requestId = requestRef.current + 1;
    requestRef.current = requestId;
    const controller = new AbortController();
    setLoading(true);
    setError(null);
    fetchChaosGroup(saveId, stageIndex, groupNumber, season, controller.signal)
      .then((loaded) => {
        if (requestRef.current !== requestId || controller.signal.aborted) {
          return;
        }
        setGroup(loaded);
        setError(null);
        setLoading(false);
      })
      .catch((failure: unknown) => {
        if (requestRef.current !== requestId) {
          // Superseded by a newer request: never clear or replace the newer's state.
          return;
        }
        if (controller.signal.aborted) {
          // Cancelled while still the latest (panel closed without reopen):
          // exit loading so reopening can start a fresh request.
          setLoading(false);
          return;
        }
        if (failure instanceof DOMException && failure.name === 'AbortError') {
          setLoading(false);
          return;
        }
        setError(apiErrorMessage(failure));
        setLoading(false);
      });
    return () => controller.abort();
  }, [open, saveId, stageIndex, groupNumber, season, currentGroup]);

  return (
    <span>
      <button type="button" className="ghost-button" aria-expanded={open} onClick={() => setOpen((value) => !value)}>
        {open ? 'Hide standings' : 'Show standings'}
      </button>
      {open ? (
        loading ? (
          <Loading label={`Loading ${isFinalChaosStage(stageIndex) ? 'Final' : `Group ${groupNumber}`}…`} />
        ) : error ? (
          <span className="muted small" role="alert">
            {' '}
            {error}
          </span>
        ) : currentGroup ? (
          <span className="table-wrap">
            <table className="data-table" aria-label={isFinalChaosStage(stageIndex) ? 'Final standings' : `Group ${groupNumber} standings`}>
              <thead>
                <tr>
                  <th scope="col" className="numeric">Rank</th>
                  <th scope="col">Card</th>
                  <th scope="col" className="numeric">Score</th>
                  <th scope="col" className="numeric">Round W</th>
                </tr>
              </thead>
              <tbody>
                {[...currentGroup.standings]
                  .sort((a, b) => a.groupRank - b.groupRank)
                  .map((row) => (
                    <GroupStandingRow
                      key={row.saveAthleteId}
                      saveId={saveId}
                      group={currentGroup}
                      athleteId={row.saveAthleteId}
                      name={row.name}
                      rank={row.groupRank}
                      score={row.groupScoreThousandths}
                      roundWins={row.roundWins}
                    />
                  ))}
              </tbody>
            </table>
            {currentGroup.cutoff ? (
              <span className="muted small">
                {' '}
                {chaosCutLabel(currentGroup.cutoff.isProvisional)} · P16 {currentGroup.cutoff.p16.name} (
                {formatChaosPoints(currentGroup.cutoff.p16.groupScoreThousandths)}) vs P17 {currentGroup.cutoff.p17.name} (
                {formatChaosPoints(currentGroup.cutoff.p17.groupScoreThousandths)}) · gap{' '}
                {formatChaosPoints(currentGroup.cutoff.gapThousandths)}.
              </span>
            ) : (
              <span className="muted small"> Final ranks every athlete 1..32; no cut.</span>
            )}
          </span>
        ) : null
      ) : null}
    </span>
  );
}

function GroupStandingRow({
  saveId,
  group,
  athleteId,
  name,
  rank,
  score,
  roundWins,
}: {
  saveId: string;
  group: ChaosCupGroup;
  athleteId: number;
  name: string;
  rank: number;
  score: number;
  roundWins: number;
}) {
  const isCutRow = !group.isFinal && rank === 16;
  return (
    <>
      <tr>
        <td className="numeric">{rank}</td>
        <td>
          <AthleteLink saveId={saveId} athleteId={athleteId} name={name} />
        </td>
        <td className="numeric">{formatChaosPoints(score)}</td>
        <td className="numeric">{roundWins}</td>
      </tr>
      {isCutRow ? (
        <tr className="cut-line" aria-hidden="true">
          <td colSpan={4} className="cut-line-cell">
            <span className="cut-line-label">{chaosCutLabel(group.cutoff?.isProvisional ?? group.standingsProvisional)}</span>
          </td>
        </tr>
      ) : null}
    </>
  );
}
