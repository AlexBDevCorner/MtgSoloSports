import { Card } from '../../shared/ui/Card';
import { AthleteLink, Link } from '../routing/router';
import { qualifierPath } from '../routing/routes';
import type { QualifierOutcomeGroup } from '../qualifiers/qualifierModel';

/**
 * Qualifier-outcome counterpart to the automatic movement board (MSS-060).
 * Automatic promotions/relegations come from the movement endpoints; these
 * cards show who the qualifiers moved — top-8 cutoff from persisted qualifier
 * standings, grouped by boundary/color with the destination tier. Read-only
 * presentation over persisted facts; full fields live on the qualifier pages.
 */
export function QualifierOutcomeSection({
  saveId,
  season,
  groups,
}: {
  saveId: string;
  /** Source season of the transition (for qualifier deep links). */
  season: number;
  groups: QualifierOutcomeGroup[];
}) {
  if (groups.length === 0) {
    return null;
  }
  return (
    <Card
      eyebrow="Qualifier outcomes"
      title={`Qualifier-decided places — ${groups.length} qualifier${groups.length === 1 ? '' : 's'}`}
      info={
        <p>
          Automatic moves above are decided by final tables; these places were decided by the
          qualifier events. Winners take places in the higher tier, eliminated athletes stay in
          (or return to) the lower tier. Full round-by-round fields live on the qualifier pages.
        </p>
      }
    >
      <div className="table-wrap">
        <table className="data-table">
          <thead>
            <tr>
              <th scope="col">Qualifier</th>
              <th scope="col">Qualified for</th>
              <th scope="col">Qualified athletes</th>
            </tr>
          </thead>
          <tbody>
            {groups.map((group) => (
              <tr key={group.key}>
                <td>
                  <Link
                    to={qualifierPath(
                      saveId,
                      group.boundary === 'Feeder1Feeder2'
                        ? 'Feeder1Feeder2'
                        : group.boundary === 'Feeder2Feeder3'
                          ? 'Feeder2Feeder3'
                          : 'Superleague',
                      group.boundary === 'Superleague' || group.sportingColorName === '-'
                        ? null
                        : group.sportingColorName,
                      { season },
                    )}
                  >
                    {group.title}
                  </Link>
                </td>
                <td>{group.destination}</td>
                <td>
                  {group.qualified.length === 0 ? (
                    <span className="muted">—</span>
                  ) : (
                    group.qualified.map((athlete, index) => (
                      <span key={athlete.athleteId}>
                        {index > 0 ? ', ' : ''}
                        <AthleteLink saveId={saveId} athleteId={athlete.athleteId} name={athlete.name} />
                      </span>
                    ))
                  )}
                  <span className="card-sub">
                    {' '}
                    · {group.qualified.length} qualified, {group.eliminatedCount} eliminated
                  </span>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </Card>
  );
}
