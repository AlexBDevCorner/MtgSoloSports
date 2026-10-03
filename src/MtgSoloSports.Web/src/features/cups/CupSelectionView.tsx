import { useCallback, useEffect, useMemo, useState } from 'react';
import { ApiError, apiErrorMessage } from '../../shared/api/http';
import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import { EVENT_TITLES, SELECTION_TITLES, type EventKey, type SelectionKey } from '../events/eventModel';
import { AthleteLink, Link } from '../routing/router';
import { cupsPath, dashboardPath, livePath } from '../routing/routes';
import {
  announceSelection,
  fetchSelectionReport,
  type SelectionCandidate,
  type SelectionReport,
  type SelectionTeam,
} from './selectionApi';
import {
  componentShares,
  explainMember,
  formatRating,
  formulaLabel,
  nextRevealTeam,
  outcomeLabel,
  revealOrder,
  revealTotals,
  selectedMembers,
} from './selectionExplain';
import '../live/LivePage.css';
import './CupSelection.css';

function allRevealed(report: SelectionReport): Record<string, number> {
  return Object.fromEntries(report.teams.map((team) => [team.teamKey, selectedMembers(team).length]));
}

/**
 * The Cup squad selection as an event on Live. The backend resolves the whole
 * selection in one saved step and stores the ranking behind it; this view only
 * reveals that stored report pick by pick and explains each pick. Reveal
 * progress is presentation state: it is never persisted and never changes who
 * was selected.
 */
export function CupSelectionView({
  saveId,
  selection,
  season,
  canAnnounce,
  nextEvent,
  onPin,
  onMutated,
}: {
  saveId: string;
  selection: SelectionKey;
  season: number;
  /** True while this selection is the save's next lifecycle step. */
  canAnnounce: boolean;
  /** The Cup event that follows this selection while it is the save's next step. */
  nextEvent: EventKey | null;
  /** Puts this selection in the URL so Live stays on it once the save moves on to the Cup. */
  onPin: () => void;
  onMutated: () => void;
}) {
  const [report, setReport] = useState<SelectionReport | null>(null);
  const [loading, setLoading] = useState(true);
  const [pending, setPending] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [revealed, setRevealed] = useState<Record<string, number>>({});
  const [activeKey, setActiveKey] = useState<string | null>(null);

  const load = useCallback(
    async (hidden: boolean, signal?: AbortSignal): Promise<void> => {
      try {
        const loaded = await fetchSelectionReport(saveId, selection, season, signal);
        setReport(loaded);
        setPending(false);
        setRevealed(hidden ? {} : allRevealed(loaded));
        setActiveKey(loaded.teams[0]?.teamKey ?? null);
      } catch (failure: unknown) {
        if (failure instanceof DOMException && failure.name === 'AbortError') {
          return;
        }
        if (failure instanceof ApiError && failure.status === 404) {
          setReport(null);
          setPending(true);
        } else {
          setError(apiErrorMessage(failure));
        }
      } finally {
        setLoading(false);
      }
    },
    [saveId, selection, season],
  );

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    // A selection that is already saved opens fully revealed; Replay hides it again.
    void load(false, controller.signal);
    return () => controller.abort();
  }, [load]);

  async function handleAnnounce(): Promise<void> {
    if (busy) {
      return;
    }
    setBusy(true);
    setError(null);
    try {
      onPin();
      await announceSelection(saveId, selection, season);
      await load(true);
      onMutated();
    } catch (failure) {
      setError(apiErrorMessage(failure));
    } finally {
      setBusy(false);
    }
  }

  const title = `${SELECTION_TITLES[selection]} · Season ${season}`;
  const totals = useMemo(() => (report ? revealTotals(report, revealed) : { shown: 0, total: 0 }), [report, revealed]);
  const done = report !== null && totals.shown >= totals.total;
  const activeTeam = report?.teams.find((team) => team.teamKey === activeKey) ?? report?.teams[0] ?? null;

  function revealNext(wholeTeam: boolean): void {
    if (!report) {
      return;
    }
    const team = nextRevealTeam(report, revealed, activeTeam?.teamKey ?? null);
    if (!team) {
      return;
    }
    const size = selectedMembers(team).length;
    const current = revealed[team.teamKey] ?? 0;
    setRevealed({ ...revealed, [team.teamKey]: wholeTeam ? size : Math.min(current + 1, size) });
    setActiveKey(team.teamKey);
  }

  if (loading && !report && !pending) {
    return <Loading label="Loading selection…" />;
  }

  return (
    <div className="live-layout">
      <aside className="live-sidebar" aria-label="Selection management">
        <Card eyebrow="Postseason event" title={title}>
          {report ? (
            <>
              <p className="muted small live-count">
                {totals.shown} / {totals.total} picks revealed · {report.teams.length} teams
              </p>
              {done ? (
                <Notice tone="info" title="Squads announced">
                  <p className="live-buttons">
                    {nextEvent ? (
                      <Link to={livePath(saveId, { event: nextEvent, season })} className="primary-button">
                        Play {EVENT_TITLES[nextEvent]}
                      </Link>
                    ) : (
                      <Link to={dashboardPath(saveId)} className="primary-button">
                        Continue on the Dashboard
                      </Link>
                    )}
                    <Link to={cupsPath(saveId)} className="ghost-button">
                      Cups
                    </Link>
                  </p>
                </Notice>
              ) : (
                <div className="live-manage-controls">
                  <div className="live-buttons">
                    <button type="button" className="primary-button" onClick={() => revealNext(false)}>
                      Reveal next pick
                    </button>
                    <button type="button" className="ghost-button" onClick={() => revealNext(true)}>
                      Reveal team
                    </button>
                    <button type="button" className="ghost-button" onClick={() => setRevealed(allRevealed(report))}>
                      Reveal all
                    </button>
                  </div>
                </div>
              )}
              {done ? (
                <p>
                  <button
                    type="button"
                    className="ghost-button"
                    onClick={() => {
                      setRevealed({});
                      setActiveKey(report.teams[0]?.teamKey ?? null);
                    }}
                  >
                    Replay the reveal
                  </button>
                </p>
              ) : null}
            </>
          ) : canAnnounce ? (
            <div className="live-manage-controls">
              <p className="muted small">
                {selection === 'color-cup-selection'
                  ? 'Every athlete is rated within its sporting color; the top four of each color make the squad.'
                  : 'Every active athlete is rated; each creature type that can field four athletes gets a team.'}
              </p>
              <div className="live-buttons">
                <button
                  type="button"
                  className="primary-button"
                  disabled={busy}
                  aria-busy={busy}
                  onClick={() => {
                    void handleAnnounce();
                  }}
                >
                  {busy ? 'Selecting…' : 'Announce the squads'}
                </button>
              </div>
              <p className="muted small">{"The selection is saved and can't be undone."}</p>
            </div>
          ) : (
            <Notice tone="info" title="Not the next event">
              <p>The squads are selected once this is the next step of the season.</p>
              <p className="live-buttons">
                <Link to={dashboardPath(saveId)} className="primary-button">
                  Continue on the Dashboard
                </Link>
              </p>
            </Notice>
          )}
          {error ? (
            <Notice tone="error" title="The selection did not complete">
              <p>{error}</p>
              <p>
                <button type="button" className="ghost-button" onClick={() => setError(null)}>
                  Dismiss
                </button>
              </p>
            </Notice>
          ) : null}
          {report ? (
            <details className="live-help">
              <summary>How athletes are rated</summary>
              <p className="muted small">Rating = {formulaLabel(report)}.</p>
              <p className="muted small">
                {selection === 'color-cup-selection'
                  ? 'Each part is measured against the other athletes of the same sporting color. Ties fall to bonus, then season, form, prestige and name.'
                  : 'Each part is measured against every active athlete. A capped athlete can only play for its type; the others go where they rank best, as long as the most full teams take part.'}
              </p>
              {!report.hasFullRanking ? (
                <p className="muted small">
                  This selection was saved before rankings were kept, so only the selected athletes are shown.
                </p>
              ) : null}
            </details>
          ) : null}
        </Card>

        {report ? (
          <Card eyebrow="Teams" title={`${report.teams.length} teams`}>
            <div className="selection-teams" role="group" aria-label="Teams">
              {report.teams.map((team) => {
                const size = selectedMembers(team).length;
                const shown = Math.min(revealed[team.teamKey] ?? 0, size);
                const current = team.teamKey === activeTeam?.teamKey;
                return (
                  <button
                    key={team.teamKey}
                    type="button"
                    className={current ? 'nav-item current' : 'nav-item'}
                    aria-pressed={current}
                    onClick={() => setActiveKey(team.teamKey)}
                  >
                    <span>{team.teamName}</span>
                    <span className="selection-team-count">
                      {shown}/{size}
                    </span>
                  </button>
                );
              })}
            </div>
            {report.missedTeams && report.missedTeams.length > 0 ? (
              <p className="muted small">
                No team this year (athletes play elsewhere):{' '}
                {report.missedTeams.map((team) => `${team.teamName} (${team.candidateCount} eligible)`).join(', ')}.
              </p>
            ) : null}
          </Card>
        ) : null}
      </aside>

      <section className="live-main" aria-label="Squad selection">
        {report && activeTeam ? (
          <TeamReveal
            saveId={saveId}
            report={report}
            team={activeTeam}
            shown={Math.min(revealed[activeTeam.teamKey] ?? 0, selectedMembers(activeTeam).length)}
          />
        ) : (
          <Card eyebrow="Postseason event" title={SELECTION_TITLES[selection]}>
            <p className="muted">
              {canAnnounce
                ? 'Press Announce the squads to select every team, then reveal them pick by pick.'
                : 'No squads have been selected for this season yet.'}
            </p>
          </Card>
        )}
      </section>
    </div>
  );
}

function TeamReveal({
  saveId,
  report,
  team,
  shown,
}: {
  saveId: string;
  report: SelectionReport;
  team: SelectionTeam;
  /** How many picks of this team are revealed, counted from the last squad number. */
  shown: number;
}) {
  const members = selectedMembers(team);
  const visible = new Set(revealOrder(team).slice(0, shown).map((member) => member.athleteId));
  const complete = shown >= members.length;
  const subtitle = report.hasFullRanking ? ` — ${team.candidateCount} in contention` : '';

  return (
    <>
      <Card eyebrow="Squad" title={`${team.teamName}${subtitle}`}>
        <ol className="selection-picks">
          {members.map((member) =>
            visible.has(member.athleteId) ? (
              <PickCard key={member.athleteId} saveId={saveId} report={report} team={team} member={member} />
            ) : (
              <li key={member.athleteId} className="selection-pick is-hidden">
                <span className="selection-pick-number">#{member.selectionRank}</span>
                <span className="muted small">Not revealed yet</span>
              </li>
            ),
          )}
        </ol>
      </Card>

      <Card eyebrow="Ranking" title={`${team.teamName} ranking`}>
        {complete ? (
          <RankingTable saveId={saveId} report={report} team={team} />
        ) : (
          <p className="muted">Reveal the whole squad to see the ranking and who just missed out.</p>
        )}
      </Card>
    </>
  );
}

function PickCard({
  saveId,
  report,
  team,
  member,
}: {
  saveId: string;
  report: SelectionReport;
  team: SelectionTeam;
  member: SelectionCandidate;
}) {
  return (
    <li className="selection-pick">
      <div className="selection-pick-head">
        {member.imageUrl ? (
          <img className="selection-pick-art" src={member.imageUrl} alt="" loading="lazy" />
        ) : (
          <span className="selection-pick-art selection-pick-art-fallback" aria-hidden="true">
            {member.name.slice(0, 2).toUpperCase()}
          </span>
        )}
        <div className="selection-pick-identity">
          <span className="selection-pick-number">#{member.selectionRank}</span>
          <AthleteLink saveId={saveId} athleteId={member.athleteId} name={member.name} />
          <span className="selection-pick-rating">Rating {formatRating(member.finalRatingThousandths)}</span>
        </div>
      </div>
      <dl className="selection-shares">
        {componentShares(report, member).map((share) => (
          <div key={share.key} className="selection-share" title={share.raw}>
            <dt>
              {share.label} <span className="muted">· {Math.round(share.weightPermille / 10)}%</span>
            </dt>
            <dd>
              <span className="selection-bar" aria-hidden="true">
                <span className="selection-bar-fill" style={{ width: `${share.normThousandths / 10}%` }} />
              </span>
              <span className="numeric">+{formatRating(share.contributionThousandths)}</span>
            </dd>
          </div>
        ))}
      </dl>
      <ul className="selection-why">
        {explainMember(report, team, member).map((line) => (
          <li key={line}>{line}</li>
        ))}
      </ul>
    </li>
  );
}

function RankingTable({ saveId, report, team }: { saveId: string; report: SelectionReport; team: SelectionTeam }) {
  return (
    <div className="table-wrap">
      <table className="data-table selection-ranking">
        <thead>
          <tr>
            <th scope="col">Rank</th>
            <th scope="col">Card</th>
            <th scope="col">Rating</th>
            <th scope="col">Bonus</th>
            <th scope="col">Season</th>
            <th scope="col">Form</th>
            <th scope="col">Prestige</th>
            <th scope="col">Outcome</th>
          </tr>
        </thead>
        <tbody>
          {team.ranking.map((row) => (
            <tr key={row.athleteId} className={row.selected ? 'is-selected' : undefined}>
              <td className="numeric">{row.rank}</td>
              <td>
                <AthleteLink saveId={saveId} athleteId={row.athleteId} name={row.name} />
                {row.capped ? <span className="card-sub"> · capped</span> : null}
              </td>
              <td className="numeric">{formatRating(row.finalRatingThousandths)}</td>
              {componentShares(report, row).map((share) => (
                <td key={share.key} className="numeric" title={share.raw}>
                  {formatRating(share.normThousandths)}
                </td>
              ))}
              <td>{outcomeLabel(team, row)}</td>
            </tr>
          ))}
        </tbody>
      </table>
      <p className="muted small">
        Bonus, Season, Form and Prestige run from 0.000 (lowest in the compared field) to 1.000 (highest); hover a value
        for the measured figure.
        {report.hasFullRanking && team.candidateCount > team.ranking.length
          ? ` Showing the top ${team.ranking.length} of ${team.candidateCount}.`
          : ''}
      </p>
    </div>
  );
}
