import { useEffect, useState } from 'react';
import { ApiError, apiErrorMessage } from '../../shared/api/http';
import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import { AthleteLink, Link } from '../routing/router';
import { cupEditionPath, cupsPath, historyPath, type CupKind } from '../routing/routes';
import {
  fetchCupTeamHistory,
  type CupTeamHistory,
  type CupTeamSeason,
  type CupTeamSquadMember,
} from './cupHistoryApi';
import {
  cupTitle,
  formatPoints,
  medalBadge,
  ordinal,
  rankOf,
  reasonLabel,
  stateLabel,
  teamEventKey,
} from './cupFormat';
import { CardArt, SquadTiles } from './SquadTiles';
import { TeamMark } from './TeamBadge';
import './CupHistory.css';

/**
 * One Cup team across every edition it was selected for: honours, the squad
 * and result of each season, and everyone who ever represented it. Read-only
 * over stored selections and results.
 */
export function CupTeamPage({ saveId, cup, teamKey }: { saveId: string; cup: CupKind; teamKey: string }) {
  const [history, setHistory] = useState<CupTeamHistory | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notFound, setNotFound] = useState(false);

  useEffect(() => {
    const controller = new AbortController();
    setHistory(null);
    setError(null);
    setNotFound(false);
    fetchCupTeamHistory(saveId, cup, teamKey, controller.signal)
      .then(setHistory)
      .catch((failure: unknown) => {
        if (failure instanceof DOMException && failure.name === 'AbortError') {
          return;
        }
        if (failure instanceof ApiError && failure.status === 404) {
          setNotFound(true);
        } else {
          setError(apiErrorMessage(failure));
        }
      });
    return () => controller.abort();
  }, [saveId, cup, teamKey]);

  if (notFound) {
    return (
      <Notice tone="warn" title="No such team">
        <p>
          “{teamKey}” has never fielded a {cupTitle(cup)} team in this save.
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
      <Notice tone="error" title="Team unavailable">
        <p>{error}</p>
      </Notice>
    );
  }
  if (!history) {
    return <Loading label="Loading team…" />;
  }

  const { honours } = history;
  return (
    <div className="dashboard">
      <header className="cup-banner">
        <div className="cup-banner-head">
          <h1 className="cup-banner-title">
            <TeamMark cup={cup} teamKey={history.teamKey} teamName={history.teamName} large />
          </h1>
          <nav className="cup-nav" aria-label="Cups">
            <Link to={cupsPath(saveId)} className="ghost-button">
              All Cups
            </Link>
          </nav>
        </div>
        <p className="muted">{cupTitle(cup)} team</p>
        <dl className="cup-facts">
          <Fact label="Cups played" value={String(honours.editions)} />
          <Fact label="Gold" value={`🥇 ${honours.gold}`} />
          <Fact label="Silver" value={`🥈 ${honours.silver}`} />
          <Fact label="Bronze" value={`🥉 ${honours.bronze}`} />
          <div className="cup-fact">
            <dt>Best finish</dt>
            <dd>
              {honours.bestRank !== null && honours.bestRankSeasonNumber !== null ? (
                <Link to={cupEditionPath(saveId, cup, honours.bestRankSeasonNumber)} className="card-link">
                  {ordinal(honours.bestRank)} · S{honours.bestRankSeasonNumber}
                </Link>
              ) : (
                '—'
              )}
            </dd>
          </div>
          <Fact label="Group wins" value={String(honours.groupWins)} />
          <Fact label="Round wins" value={String(honours.roundWins)} />
          <Fact label="Total score" value={formatPoints(honours.totalScoreThousandths)} />
        </dl>
      </header>

      <Card
        eyebrow="Seasons"
        title={`${history.seasons.length} ${history.seasons.length === 1 ? 'Cup' : 'Cups'}`}
        info={
          <p>
            Each squad member plays one rank group: #1 against every other team's #1, and so on. The rating is the
            stored selection rating with its four normalized components.
          </p>
        }
      >
        {history.seasons.map((season) => (
          <SeasonBlock
            key={`${season.sourceSeasonNumber}:${season.tournamentPhase ?? 0}:${season.qualificationGroup ?? 0}`}
            saveId={saveId}
            cup={cup}
            season={season}
          />
        ))}
      </Card>

      <div className="page-grid">
        <Card eyebrow="Everyone who played" title={`All-time roster — ${history.roster.length}`}>
          <div className="table-wrap">
            <table className="data-table">
              <thead>
                <tr>
                  <th scope="col">Card</th>
                  <th scope="col" className="numeric">Caps</th>
                  <th scope="col" className="numeric">Seasons</th>
                  <th scope="col" className="numeric">Leg score</th>
                  <th scope="col" className="numeric">Best leg</th>
                </tr>
              </thead>
              <tbody>
                {history.roster.map((entry) => (
                  <tr key={entry.athleteId}>
                    <td>
                      <span className="roster-name">
                        <CardArt imageUrl={entry.imageUrl} name={entry.name} small />
                        <AthleteLink saveId={saveId} athleteId={entry.athleteId} name={entry.name} />
                      </span>
                    </td>
                    <td className="numeric">{entry.caps}</td>
                    <td className="numeric">
                      {entry.firstSeasonNumber === entry.lastSeasonNumber
                        ? `S${entry.firstSeasonNumber}`
                        : `S${entry.firstSeasonNumber}–S${entry.lastSeasonNumber}`}
                    </td>
                    <td className="numeric">{formatPoints(entry.totalLegScoreThousandths)}</td>
                    <td className="numeric">{entry.bestGroupRank === null ? '—' : ordinal(entry.bestGroupRank)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </Card>

        {cup === 'color' ? (
          <Card eyebrow="Individual event" title={`Individual medals — ${history.individualMedals.length}`}>
            {history.individualMedals.length === 0 ? (
              <p className="muted">No athlete of this team has won an individual Color Cup medal.</p>
            ) : (
              <div className="table-wrap">
                <table className="data-table">
                  <thead>
                    <tr>
                      <th scope="col" className="numeric">Season</th>
                      <th scope="col">Card</th>
                      <th scope="col">Medal</th>
                    </tr>
                  </thead>
                  <tbody>
                    {history.individualMedals.map((entry) => (
                      <tr key={`${entry.sourceSeasonNumber}:${entry.athleteId}`}>
                        <td className="numeric">
                          <Link to={cupEditionPath(saveId, cup, entry.sourceSeasonNumber)} className="card-link">
                            S{entry.sourceSeasonNumber}
                          </Link>
                        </td>
                        <td>
                          <AthleteLink saveId={saveId} athleteId={entry.athleteId} name={entry.name} />
                        </td>
                        <td>{medalBadge(entry.medal)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </Card>
        ) : null}
      </div>
    </div>
  );
}

function Fact({ label, value }: { label: string; value: string }) {
  return (
    <div className="cup-fact">
      <dt>{label}</dt>
      <dd>{value}</dd>
    </div>
  );
}

function SeasonBlock({ saveId, cup, season }: { saveId: string; cup: CupKind; season: CupTeamSeason }) {
  const stageBadge =
    cup === 'type' && season.tournamentStage
      ? ` · ${season.tournamentStage}`
      : '';
  const fateBadge =
    cup === 'type' && season.eliminatedInQualification
      ? ' · Eliminated'
      : cup === 'type' && season.qualifiedForFinal
        ? ' · Qualified for Final'
        : '';
  return (
    <section
      className="team-season"
      aria-label={season.tournamentStage ? `Season ${season.sourceSeasonNumber} ${season.tournamentStage}` : `Season ${season.sourceSeasonNumber}`}
    >
      <div className="team-season-head">
        <Link to={cupEditionPath(saveId, cup, season.sourceSeasonNumber)} className="card-name card-link">
          Season {season.sourceSeasonNumber}
        </Link>
        {season.teamRank !== null ? (
          <span className="team-season-result">
            {rankOf(season.teamRank, season.teamCount)}
            {stageBadge}
            {fateBadge}
            {season.medal && season.medal !== 'None' ? ` · ${medalBadge(season.medal)}` : ''}
            {season.teamScoreThousandths !== null ? ` · ${formatPoints(season.teamScoreThousandths)}` : ''}
          </span>
        ) : (
          <span className="team-season-result">{stateLabel(season.state)} — no result yet</span>
        )}
      </div>
      {cup === 'type' && season.tournamentStage ? (
        <p className="muted small">
          {season.eliminatedInQualification
            ? 'Eliminated in qualification: the team keeps this Cup appearance, but only Final ranks carry medals and honours.'
            : season.qualifiedForFinal
              ? 'Reached the fresh 32-team Final: qualification points reset and only Final ranks carry medals and honours.'
              : 'Tournament stage from persisted results.'}
        </p>
      ) : null}
      <SquadTiles
        saveId={saveId}
        members={season.squad}
        renderDetail={(member) => <MemberDetail saveId={saveId} cup={cup} season={season} member={member} />}
      />
    </section>
  );
}

function MemberDetail({
  saveId,
  cup,
  season,
  member,
}: {
  saveId: string;
  cup: CupKind;
  season: CupTeamSeason;
  member: CupTeamSquadMember;
}) {
  const reason = reasonLabel(member.reason);
  const stagePrefix =
    cup === 'type' && season.tournamentStage ? `${season.tournamentStage} · Squad #${member.leg?.groupNumber ?? '?'} leg: ` : null;
  return (
    <>
      {member.leg ? (
        <Link
          to={historyPath(saveId, {
            season: season.sourceSeasonNumber,
            event: teamEventKey(cup),
            group: member.leg.groupNumber,
          })}
          className="card-link"
          title="Replay this rank group in History"
        >
          {stagePrefix ?? `Leg: ${rankOf(member.leg.groupRank, member.leg.groupSize)} · `}
          {stagePrefix
            ? `${ordinal(member.leg.groupRank)} · ${formatPoints(member.leg.groupScoreThousandths)}`
            : `${formatPoints(member.leg.groupScoreThousandths)}`}
        </Link>
      ) : (
        <span>Leg not played yet</span>
      )}
      {member.individual ? (
        <span>
          Individual: {ordinal(member.individual.cupRank)}
          {member.individual.medal !== 'None' ? ` ${medalBadge(member.individual.medal)}` : ''}
        </span>
      ) : null}
      <span title="Selection rating and its normalized components">
        Rating {formatPoints(member.finalRatingThousandths)} · B {formatPoints(member.bonusNormThousandths)} · P{' '}
        {formatPoints(member.performanceNormThousandths)} · F {formatPoints(member.formNormThousandths)} · C{' '}
        {formatPoints(member.prestigeNormThousandths)}
      </span>
      {reason ? <span>{reason}</span> : null}
    </>
  );
}
