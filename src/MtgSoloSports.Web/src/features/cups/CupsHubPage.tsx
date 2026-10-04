import { useEffect, useState } from 'react';
import { apiErrorMessage } from '../../shared/api/http';
import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import { fetchSeasonStatus, type SeasonStatus } from '../dashboard/dashboardApi';
import { SELECTION_TITLES, selectionForAction } from '../events/eventModel';
import { Link } from '../routing/router';
import { cupEditionPath, livePath, type CupKind } from '../routing/routes';
import { fetchCupEditions, type CupEdition, type CupEditions, type CupTeamSummary } from './cupHistoryApi';
import { cupKindOf, cupTitle, medalBadge, ordinal, stateLabel } from './cupFormat';
import { EventLiveAction } from './EventLiveAction';
import { CardArt } from './SquadTiles';
import { TeamBadge, TeamMark } from './TeamBadge';
import './CupHistory.css';

/**
 * Cups hub: every Cup edition ever played and every team that played one,
 * each linking to its own page. Playing a Cup stays on Live; while a Cup step
 * is the save's next lifecycle step the hub links there.
 */
export function CupsHubPage({ saveId }: { saveId: string }) {
  const [data, setData] = useState<CupEditions | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [status, setStatus] = useState<SeasonStatus | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    setError(null);
    fetchCupEditions(saveId, controller.signal)
      .then(setData)
      .catch((failure: unknown) => {
        if (failure instanceof DOMException && failure.name === 'AbortError') {
          return;
        }
        setError(apiErrorMessage(failure));
      });
    fetchSeasonStatus(saveId, controller.signal)
      .then(setStatus)
      .catch(() => setStatus(null));
    return () => controller.abort();
  }, [saveId]);

  if (error) {
    return (
      <Notice tone="error" title="Cups unavailable">
        <p>{error}</p>
      </Notice>
    );
  }
  if (!data) {
    return <Loading label="Loading Cups…" />;
  }

  const pendingSelection = selectionForAction(status?.legalNextActions[0]);
  const pendingSeason = status?.sourceSeasonNumber ?? null;
  const cupEvent = status?.eventProgress && status.eventProgress.event !== 'qualifier' ? status.eventProgress : null;

  return (
    <div className="dashboard">
      {pendingSelection || cupEvent ? (
        <Card eyebrow="Next step" title={pendingSelection ? SELECTION_TITLES[pendingSelection] : 'Cup event'}>
          {pendingSelection && pendingSeason !== null ? (
            <>
              <p className="muted">Season {pendingSeason}: the squads are ready to be selected.</p>
              <p>
                <Link to={livePath(saveId, { event: pendingSelection, season: pendingSeason })} className="primary-button">
                  Announce on Live
                </Link>
              </p>
            </>
          ) : null}
          <EventLiveAction saveId={saveId} eventKey="color-cup-individual" status={status} />
          <EventLiveAction saveId={saveId} eventKey="color-cup-team" status={status} />
          <EventLiveAction saveId={saveId} eventKey="type-cup-team" status={status} />
        </Card>
      ) : null}

      {data.editions.length === 0 ? (
        <Notice tone="empty" title="No Cups have been played yet.">
          <p>The Color Cup follows every odd season and the Type Cup every even season.</p>
        </Notice>
      ) : (
        <>
          {(['color', 'type'] as const).map((cup) => (
            <EditionSection
              key={cup}
              saveId={saveId}
              cup={cup}
              editions={data.editions.filter((edition) => cupKindOf(edition.cup) === cup)}
            />
          ))}
          <div className="page-grid">
            <TeamTable saveId={saveId} cup="color" teams={data.colorTeams} />
            <TeamTable saveId={saveId} cup="type" teams={data.typeTeams} />
          </div>
        </>
      )}
    </div>
  );
}

function EditionSection({ saveId, cup, editions }: { saveId: string; cup: CupKind; editions: CupEdition[] }) {
  if (editions.length === 0) {
    return null;
  }
  return (
    <section className="page-section" aria-labelledby={`cups-${cup}`}>
      <h2 id={`cups-${cup}`} className="section-title">
        {cupTitle(cup)} — {editions.length} {editions.length === 1 ? 'edition' : 'editions'}
      </h2>
      <ul className="edition-cards">
        {editions.map((edition) => (
          <li key={edition.sourceSeasonNumber}>
            <EditionCard saveId={saveId} cup={cup} edition={edition} />
          </li>
        ))}
      </ul>
    </section>
  );
}

function EditionCard({ saveId, cup, edition }: { saveId: string; cup: CupKind; edition: CupEdition }) {
  return (
    <Link
      to={cupEditionPath(saveId, cup, edition.sourceSeasonNumber)}
      className="edition-card"
      title={`Open the Season ${edition.sourceSeasonNumber} ${cupTitle(cup)}`}
    >
      <span className="edition-card-season">Season {edition.sourceSeasonNumber}</span>
      <span className="edition-card-state">
        {stateLabel(edition.state)} · {edition.teamCount} teams
      </span>
      {edition.podium.length > 0 ? (
        <ol className="edition-card-podium">
          {edition.podium.map((team) => (
            <li key={team.teamKey}>
              {medalBadge(team.medal)} <TeamMark cup={cup} teamKey={team.teamKey} teamName={team.teamName} />
            </li>
          ))}
        </ol>
      ) : null}
      {edition.individualChampion ? (
        <span className="edition-card-champion">
          <CardArt imageUrl={edition.individualChampion.imageUrl} name={edition.individualChampion.name} small />
          <span>Individual: {edition.individualChampion.name}</span>
        </span>
      ) : null}
    </Link>
  );
}

function TeamTable({ saveId, cup, teams }: { saveId: string; cup: CupKind; teams: CupTeamSummary[] }) {
  return (
    <Card
      eyebrow={cupTitle(cup)}
      title={`All-time teams — ${teams.length}`}
      info={<p>Every team that was selected for at least one {cupTitle(cup)}, ordered by medals won.</p>}
    >
      {teams.length === 0 ? (
        <p className="muted">No {cupTitle(cup)} has been played yet.</p>
      ) : (
        <div className="table-wrap">
          <table className="data-table">
            <thead>
              <tr>
                <th scope="col">Team</th>
                <th scope="col" className="numeric">Cups</th>
                <th scope="col" className="numeric">🥇</th>
                <th scope="col" className="numeric">🥈</th>
                <th scope="col" className="numeric">🥉</th>
                <th scope="col" className="numeric">Best</th>
                <th scope="col" className="numeric">Last</th>
              </tr>
            </thead>
            <tbody>
              {teams.map((team) => (
                <tr key={team.teamKey}>
                  <td>
                    <TeamBadge saveId={saveId} cup={cup} teamKey={team.teamKey} teamName={team.teamName} />
                  </td>
                  <td className="numeric">{team.editions}</td>
                  <td className="numeric">{team.gold}</td>
                  <td className="numeric">{team.silver}</td>
                  <td className="numeric">{team.bronze}</td>
                  <td className="numeric">{team.bestRank === null ? '—' : ordinal(team.bestRank)}</td>
                  <td className="numeric">S{team.lastSeasonNumber}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Card>
  );
}
