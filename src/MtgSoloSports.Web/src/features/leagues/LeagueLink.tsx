import type { ReactNode } from 'react';
import { Link } from '../routing/router';
import { leaguePath } from '../routing/routes';

/**
 * MSS-138 small league-link component for the athlete profile slice.
 * Links one league name to its cross-season league profile via the stable
 * league key. Callers render plain text when the key is missing so older
 * payloads never produce a guessed URL. Reusable by MSS-139 for other pages.
 * Uses the shared router anchor so Ctrl/Cmd-click and middle-click open a
 * new tab; table styling is inherited and the global :focus-visible outline
 * keeps the link keyboard-visible.
 */
export function LeagueLink({
  saveId,
  leagueKey,
  children,
  title,
  className,
}: {
  saveId: string;
  leagueKey: string;
  children: ReactNode;
  title?: string;
  className?: string;
}) {
  return (
    <Link
      to={leaguePath(saveId, leagueKey)}
      title={title ?? 'Open the league profile'}
      className={className}
    >
      {children}
    </Link>
  );
}
