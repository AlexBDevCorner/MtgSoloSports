import { Link } from '../routing/router';
import { cupTeamPath, type CupKind } from '../routing/routes';
import { teamSwatchClass } from './cupFormat';
import './CupHistory.css';

interface TeamIdentity {
  cup: CupKind;
  teamKey: string;
  teamName: string;
  large?: boolean;
}

/** Team identity without navigation, for use inside another link or a heading. */
export function TeamMark({ cup, teamKey, teamName, large }: TeamIdentity) {
  return (
    <span className={large ? 'team-mark team-large' : 'team-mark'}>
      <span className={teamSwatchClass(cup, teamKey)} aria-hidden="true" />
      {teamName}
    </span>
  );
}

/** Team identity linking to that team's history page. */
export function TeamBadge({ saveId, cup, teamKey, teamName, large }: TeamIdentity & { saveId: string }) {
  return (
    <Link
      to={cupTeamPath(saveId, cup, teamKey)}
      className={large ? 'team-badge team-large' : 'team-badge'}
      title={`Open the ${teamName} team history`}
    >
      <span className={teamSwatchClass(cup, teamKey)} aria-hidden="true" />
      {teamName}
    </Link>
  );
}
