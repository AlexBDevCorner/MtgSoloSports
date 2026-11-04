import { useEffect, useState } from 'react';
import { ApiError, apiErrorMessage } from '../../shared/api/http';
import { Card } from '../../shared/ui/Card';
import { Loading } from '../../shared/ui/Notice';
import { AthleteLink, Link } from '../routing/router';
import { cupEditionPath } from '../routing/routes';
import { fetchChaosCupRecords, type ChaosCupRecords } from './chaosCupApi';

function storyKindLabel(kind: string): string {
  if (kind === 'defense_failed') {
    return 'Title defense failed';
  }
  if (kind === 'record_run') {
    return 'Record run';
  }
  if (kind === 'underdog') {
    return 'Underdog';
  }
  return kind;
}

function bestRankDisplay(value: number | null): string {
  return value === null || value === undefined ? '—' : `P${value}`;
}

/**
 * Factual Chaos records plus conditional Tournament Stories (MSS-117).
 * Read-only presentation over persisted completed editions: titles, medals,
 * finals, consecutive progress, division/color bests and repeated P16
 * survivals, with source-linked story cards for defended-title failures,
 * record-setting runs and conservative underdogs. Empty states are valid;
 * nothing here affects points, bonus or advancement.
 */
export function ChaosRecordsPanel({ saveId }: { saveId: string }) {
  const [records, setRecords] = useState<ChaosCupRecords | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError(null);
    fetchChaosCupRecords(saveId, controller.signal)
      .then((loaded) => {
        if (!controller.signal.aborted) {
          setRecords(loaded);
          setLoading(false);
        }
      })
      .catch((failure: unknown) => {
        if (failure instanceof DOMException && failure.name === 'AbortError') {
          return;
        }
        if (!controller.signal.aborted) {
          if (failure instanceof ApiError && failure.status === 404) {
            setRecords(null);
          } else {
            setError(apiErrorMessage(failure));
          }
          setLoading(false);
        }
      });
    return () => {
      controller.abort();
    };
  }, [saveId]);

  if (loading && !records) {
    return <Loading label="Loading Chaos records…" />;
  }
  if (error) {
    return (
      <Card eyebrow="Chaos records" title="Tournament history unavailable">
        <p className="muted">{error}</p>
      </Card>
    );
  }
  if (!records) {
    return (
      <Card
        eyebrow="Chaos records"
        title="No Chaos history yet"
        info={<p>Complete a Chaos Cup edition to populate all-time Chaos records.</p>}
      >
        <p className="muted">No Chaos Cup edition exists in this universe yet.</p>
      </Card>
    );
  }

  const athletes = Array.isArray(records.athletes) ? records.athletes : [];
  const stories = Array.isArray(records.stories) ? records.stories : [];
  const completedSeasons = Array.isArray(records.completedSeasons) ? records.completedSeasons : [];
  const leaders = athletes.filter((row) => row.titles > 0 || row.medals > 0 || row.finals > 0).slice(0, 10);
  const completed = completedSeasons.length > 0 ? completedSeasons.join(', ') : 'none yet';

  const thresholdsVersion = typeof records.thresholdsVersion === 'string' ? records.thresholdsVersion : 'MSS-117-v1';
  return (
    <div className="page-grid">
      <Card
        eyebrow="Chaos records"
        title={`All-time Chaos leaders — ${completedSeasons.length} completed edition${completedSeasons.length === 1 ? '' : 's'}`}
        info={
          <p>
            Titles count Final wins, medals count Final P1..P3, finals count Final appearances. Consecutive
            progress is the longest run of consecutive completed editions survived from the opening stage.
            Completed seasons: {completed}. Thresholds {thresholdsVersion}.
          </p>
        }
      >
        {leaders.length === 0 ? (
          <p className="muted">No completed Chaos edition yet; records are vacant.</p>
        ) : (
          <div className="table-wrap">
            <table className="data-table">
              <thead>
                <tr>
                  <th scope="col">Card</th>
                  <th scope="col" className="numeric">Titles</th>
                  <th scope="col" className="numeric">Medals</th>
                  <th scope="col" className="numeric">Finals</th>
                  <th scope="col">Best</th>
                  <th scope="col" className="numeric">Streak</th>
                  <th scope="col" className="numeric">P16</th>
                </tr>
              </thead>
              <tbody>
                {leaders.map((row) => (
                  <tr key={row.saveAthleteId}>
                    <td>
                      <AthleteLink saveId={saveId} athleteId={row.saveAthleteId} name={row.athleteName} />
                    </td>
                    <td className="numeric">{row.titles}</td>
                    <td className="numeric">{row.medals}</td>
                    <td className="numeric">{row.finals}</td>
                    <td>{bestRankDisplay(row.bestFinalRank)}</td>
                    <td className="numeric">{row.longestProgressStreak}</td>
                    <td className="numeric">{row.p16Survivals}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>

      <Card
        eyebrow="Tournament stories"
        title={stories.length === 0 ? 'No stories this time' : `${stories.length} stor${stories.length === 1 ? 'y' : 'ies'}`}
        info={
          <p>
            Conditional cards only: defended-title failures need the reigning champion entered in the next
            completed edition, underdogs need a Pool or Feeder 3 entrant in the Final, and record runs need a
            repeat-level all-time best. Group outranking alone never qualifies; stories never affect scoring.
          </p>
        }
      >
        {stories.length === 0 ? (
          <p className="muted">
            No Tournament Stories meet the conservative thresholds yet. Factual history stays without invented
            duels or points.
          </p>
        ) : (
          <ul className="story-list">
            {stories.map((story, index) => (
              <li key={`${story.kind}-${story.saveAthleteId ?? 0}-${story.sourceSeasonNumber}-${index}`}>
                <span className="badge badge-ready">{storyKindLabel(story.kind)}</span>{' '}
                {story.saveAthleteId !== null ? (
                  <AthleteLink saveId={saveId} athleteId={story.saveAthleteId} name={story.athleteName} />
                ) : (
                  <span>{story.athleteName}</span>
                )}{' '}
                <span>{story.text}</span>{' '}
                <span className="card-sub">
                  <Link to={cupEditionPath(saveId, 'chaos', story.sourceSeasonNumber)}>
                    Season {story.sourceSeasonNumber}
                  </Link>
                  {story.relatedSeasonNumber !== null ? (
                    <>
                      {' · '}
                      <Link to={cupEditionPath(saveId, 'chaos', story.relatedSeasonNumber)}>
                        Season {story.relatedSeasonNumber}
                      </Link>
                    </>
                  ) : null}
                  {' · '}
                  {story.detail}
                </span>
              </li>
            ))}
          </ul>
        )}
      </Card>
    </div>
  );
}
