import { useEffect, useMemo, useState } from 'react';
import { apiErrorMessage, ApiError } from '../../shared/api/http';
import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import { groupLeaguesByTier, leagueLevelLabel, pyramidSummary } from '../../shared/leagueTiers';
import { AthleteLink, Link } from '../routing/router';
import { leaguePath } from '../routing/routes';
import { formatPoints } from '../standings/matrix';
import { fetchLeagueProfiles, type LeagueProfiles, type LeagueProfileSummary } from './leaguesApi';
import './Leagues.css';

/**
 * Leagues overview: every official league identity in the selected universe,
 * grouped by tier, each linking to its dedicated historical profile. Mirrors
 * the Cups hub structure: one aggregated request, card grid, medal context.
 */
export function LeaguesOverviewPage({ saveId }: { saveId: string }) {
  const [data, setData] = useState<LeagueProfiles | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    setError(null);
    setData(null);
    fetchLeagueProfiles(saveId, controller.signal)
      .then((loaded) => {
        if (loaded.saveId !== saveId) {
          return;
        }
        setData(loaded);
      })
      .catch((failure: unknown) => {
        if (failure instanceof DOMException && failure.name === 'AbortError') {
          return;
        }
        if (failure instanceof ApiError && failure.status === 404) {
          setData(null);
          setError(null);
          return;
        }
        setError(apiErrorMessage(failure));
      });
    return () => controller.abort();
  }, [saveId]);

  const leagues = useMemo(
    () => (data && data.saveId === saveId ? data.leagues : []),
    [data, saveId],
  );
  // All derivations run unconditionally so hook order stays stable across
  // loading/error/empty states (same rule as StandingsPage).
  const tierRefs = useMemo(
    () =>
      leagues.map((row, index) => ({
        leagueId: index,
        key: row.leagueKey,
        name: row.name,
        kind: row.kind,
        feederDivision: row.feederDivision,
        leagueLevel: row.leagueLevel,
      })),
    [leagues],
  );
  const groups = useMemo(() => groupLeaguesByTier(tierRefs), [tierRefs]);
  const byKey = useMemo(() => new Map(leagues.map((row) => [row.leagueKey, row])), [leagues]);
  const sections = useMemo(
    () =>
      (
        [
          { title: 'Superleague', keys: groups.superleague.map((row) => row.key) },
          { title: 'Feeder 1', keys: groups.feeder1.map((row) => row.key) },
          { title: 'Feeder 2', keys: groups.feeder2.map((row) => row.key) },
          { title: 'Feeder 3', keys: groups.feeder3.map((row) => row.key) },
          { title: 'Feeder', keys: groups.legacyFeeder.map((row) => row.key) },
        ] as Array<{ title: string; keys: string[] }>
      ).filter((section) => section.keys.length > 0),
    [groups],
  );
  const pyramidLine = useMemo(() => pyramidSummary(tierRefs), [tierRefs]);

  if (error) {
    return (
      <Notice tone="error" title="Leagues unavailable">
        <p>{error}</p>
      </Notice>
    );
  }
  if (!data) {
    return <Loading label="Loading leagues…" />;
  }
  if (leagues.length === 0) {
    return (
      <Notice tone="empty" title="No leagues yet">
        <p>This save has no recorded leagues. Complete a season to persist official standings.</p>
      </Notice>
    );
  }

  return (
    <div className="dashboard">
      <Card
        eyebrow="Leagues"
        title={`Official leagues — ${leagues.length}`}
        info={
          <p>
            Every league with recorded final standings in this universe, grouped by tier. Histories stay
            credited to the exact league and season where they happened: promotion, relegation and
            division changes never reassign achievements, and distinct feeder divisions never merge.
            Pool results are not an official league and never appear here. {pyramidLine}
          </p>
        }
      >
        {sections.map((section) => (
          <section key={section.title} className="page-section" aria-label={`${section.title} leagues`}>
            <h2 className="section-title">
              {section.title} — {section.keys.length} {section.keys.length === 1 ? 'league' : 'leagues'}
            </h2>
            <ul className="league-cards">
              {section.keys.map((key) => {
                const league = byKey.get(key);
                if (!league) {
                  return null;
                }
                return (
                  <li key={key}>
                    <LeagueCard saveId={saveId} league={league} />
                  </li>
                );
              })}
            </ul>
          </section>
        ))}
      </Card>
    </div>
  );
}

function LeagueCard({ saveId, league }: { saveId: string; league: LeagueProfileSummary }) {
  const tier = leagueLevelLabel(league.leagueLevel, league.feederDivision, league.kind);
  return (
    <Link
      to={leaguePath(saveId, league.leagueKey)}
      className="league-card"
      title={`Open the ${league.name} profile`}
    >
      <span className="league-card-season">{league.name}</span>
      <span className="league-card-state">
        {tier} · {league.completedSeasons} {league.completedSeasons === 1 ? 'season' : 'seasons'} ·{' '}
        {league.uniqueAthletes} {league.uniqueAthletes === 1 ? 'athlete' : 'athletes'}
      </span>
      {league.latestChampion ? (
        <span className="league-card-champion">
          <span aria-hidden="true">🥇</span>{' '}
          <span>
            S{league.latestChampion.seasonNumber}: {league.latestChampion.athleteName} ·{' '}
            {formatPoints(league.latestChampion.totalChampionshipPointsThousandths)}
          </span>
        </span>
      ) : (
        <span className="league-card-champion muted">No completed season yet</span>
      )}
    </Link>
  );
}

/** Latest champions across the overview always link onward to career profiles. */
export function LeagueChampionLink({ saveId, league }: { saveId: string; league: LeagueProfileSummary }) {
  if (!league.latestChampion) {
    return null;
  }
  return (
    <AthleteLink
      saveId={saveId}
      athleteId={league.latestChampion.athleteId}
      name={league.latestChampion.athleteName}
    />
  );
}
