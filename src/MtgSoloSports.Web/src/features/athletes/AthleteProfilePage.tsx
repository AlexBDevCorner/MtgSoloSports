import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import type { AthleteProfileState } from './useAthleteProfile';

/** Display-only projection of a fixed-point thousandths value (no sporting math). */
function formatPoints(thousandths: number): string {
  return (thousandths / 1000).toFixed(3);
}

/** Display-only projection of a fixed-point bonus (no sporting math). */
function formatBonus(thousandths: number): string {
  const sign = thousandths >= 0 ? '+' : '';
  return `${sign}${(thousandths / 1000).toFixed(3)}`;
}

export function AthleteProfilePage({
  saveId,
  athleteId,
  state,
  hasSelection,
  onBackToLive,
  onGoToSaves,
}: {
  saveId: string | null;
  athleteId: number | null;
  state: AthleteProfileState;
  hasSelection: boolean;
  onBackToLive: () => void;
  onGoToSaves: () => void;
}) {
  if (!hasSelection || !saveId || athleteId === null) {
    return (
      <Notice tone="empty" title="No athlete selected">
        <p>Pick a card row in Live rounds or standings to open its career profile.</p>
        <p>
          <button type="button" className="primary-button" onClick={onGoToSaves}>
            Go to saves
          </button>
        </p>
      </Notice>
    );
  }

  if (state.loading && !state.profile) {
    return <Loading label="Loading athlete profile…" />;
  }

  if (state.notFound || (state.error && !state.profile)) {
    return (
      <Notice tone="error" title="Athlete unavailable">
        <p>{state.error ?? 'That athlete no longer exists.'}</p>
        <p>
          <button type="button" className="ghost-button" onClick={onBackToLive}>
            Back to live
          </button>
        </p>
      </Notice>
    );
  }

  const profile = state.profile;
  if (!profile) {
    return <Loading label="Loading athlete profile…" />;
  }

  const { card, career, seasons } = profile;
  const statusLabel = career.isActive
    ? `Active · ${career.currentLeagueName ?? 'League'}`
    : 'Common pool';

  return (
    <div className="dashboard">
      <Card
        eyebrow="Athlete profile"
        title={card.name}
        action={
          <button type="button" className="ghost-button" onClick={onBackToLive}>
            Back to live
          </button>
        }
      >
        <div className="card-cell">
          {card.imageUrl ? (
            <img className="card-thumb" src={card.imageUrl} alt="" loading="lazy" />
          ) : (
            <span className="card-thumb card-thumb-fallback" aria-hidden="true">
              {card.name.slice(0, 2).toUpperCase()}
            </span>
          )}
          <span className="card-identity">
            <span className="card-name">{card.typeLine}</span>
            <span className="card-sub">
              {card.sportingColorName} · {card.manaCost || 'No cost'} ·{' '}
              {card.creatureTypes.join(', ') || 'Unknown type'}
              {card.setCode ? ` · ${card.setCode}` : ''}
            </span>
            <span className="card-sub">{statusLabel}</span>
          </span>
        </div>
        {state.error ? (
          <Notice tone="warn" title="Showing last loaded state">
            <p>{state.error}</p>
          </Notice>
        ) : null}
      </Card>

      <div className="page-grid cards-3">
        <Card eyebrow="Career" title={`${career.seasonsActive} season(s) active`}>
          <dl className="stats">
            <div>
              <dt>Status</dt>
              <dd>{statusLabel}</dd>
            </div>
            <div>
              <dt>Round wins</dt>
              <dd>{career.roundWins}</dd>
            </div>
            <div>
              <dt>Stage wins</dt>
              <dd>{career.stageWins}</dd>
            </div>
            <div>
              <dt>Stage podiums</dt>
              <dd>
                {career.stagePodiums} ({career.stageWins}W · {career.stageSeconds}2nd ·{' '}
                {career.stageThirds}3rd)
              </dd>
            </div>
            <div>
              <dt>Best finish</dt>
              <dd>
                {career.bestSeasonFinish !== null && career.bestSeasonNumber !== null
                  ? `P${career.bestSeasonFinish} (Season ${career.bestSeasonNumber})`
                  : '—'}
              </dd>
            </div>
          </dl>
        </Card>

        <Card eyebrow="Bonus" title="Effective vs lifetime">
          <dl className="stats">
            <div>
              <dt>Current effective</dt>
              <dd>{formatBonus(career.currentEffectiveBonusThousandths)}</dd>
            </div>
            <div>
              <dt>Lifetime earned</dt>
              <dd>{formatBonus(career.lifetimeEarnedBonusThousandths)}</dd>
            </div>
            <div>
              <dt>Updated through</dt>
              <dd>
                Season {career.lastSeasonNumber} · Stage {career.lastStageNumber}
              </dd>
            </div>
          </dl>
          <p className="muted small">
            Effective bonus activates from the next stage; Stage 32 bonus enters the next
            season at 80% weight. Lifetime earned never decays.
          </p>
        </Card>

        <Card eyebrow="Identity" title="Sporting metadata">
          <dl className="stats">
            <div>
              <dt>Sporting color</dt>
              <dd>{card.sportingColorName}</dd>
            </div>
            <div>
              <dt>Athlete id</dt>
              <dd>{profile.athleteId}</dd>
            </div>
            <div>
              <dt>Front colors</dt>
              <dd>{card.frontColors || '—'}</dd>
            </div>
          </dl>
        </Card>
      </div>

      <Card eyebrow="History" title={`Season-by-season — ${seasons.length} season(s)`}>
        <div className="table-wrap">
          <table className="data-table">
            <thead>
              <tr>
                <th scope="col">Season</th>
                <th scope="col">Status</th>
                <th scope="col">Finish</th>
                <th scope="col">Round W</th>
                <th scope="col">Stage W</th>
                <th scope="col">2nd/3rd</th>
                <th scope="col">Earned</th>
                <th scope="col">Champ pts</th>
              </tr>
            </thead>
            <tbody>
              {seasons.map((season) => (
                <tr key={season.seasonNumber}>
                  <td className="numeric">{season.seasonNumber}</td>
                  <td>{season.wasActive ? (season.leagueName ?? 'Active') : 'Pool'}</td>
                  <td className="numeric">
                    {season.seasonRank !== null ? `P${season.seasonRank}` : '—'}
                    {season.isChampion ? ' · Champion' : ''}
                  </td>
                  <td className="numeric">{season.roundWins}</td>
                  <td className="numeric">{season.stageWins}</td>
                  <td className="numeric">
                    {season.stageSeconds}/{season.stageThirds}
                  </td>
                  <td className="numeric">{formatBonus(season.earnedBonusThousandths)}</td>
                  <td className="numeric">
                    {formatPoints(season.totalChampionshipPointsThousandths)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <p className="muted small">
          Season summaries are transactional projections rebuilt from authoritative stage
          and season standings; round-by-round replay stays in Live rounds.
        </p>
      </Card>

      <Card eyebrow="Stories" title="Sporting stories">
        {state.stories.length === 0 ? (
          <p className="muted">
            No stories yet for this athlete. First stage wins, titles, Superleague
            milestones and pool returns appear here.
          </p>
        ) : (
          <ul className="story-list">
            {state.stories.map((story) => (
              <li key={story.id}>
                <span className="badge badge-ready">{story.eventType}</span>{' '}
                <span>{story.text}</span>
              </li>
            ))}
          </ul>
        )}
      </Card>
    </div>
  );
}
