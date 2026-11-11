import { TeamBadge } from './TeamBadge';
import { colorTeamKeyFromName, colorTeamKeyFromSportingColor } from './cupTeamKeys';

/**
 * MSS-139 small color-team link for sporting color attributes shown on
 * leaders, standings, search and qualifier rows. Links the color name to its
 * Color Cup team page through the existing `TeamBadge` anchor; unknown or
 * legacy identities render as plain text so no URL is ever guessed. Uses the
 * shared router anchor, so Ctrl/Cmd-click and middle-click open a new tab and
 * table styling is unchanged.
 */
export function ColorTeamLink({
  saveId,
  sportingColor,
  sportingColorName,
}: {
  saveId: string;
  /** Numeric sporting color when the row carries one (may be absent on legacy payloads). */
  sportingColor?: number | null;
  /** Display name; also the fallback identity for string-only rows. */
  sportingColorName: string;
}) {
  const teamKey =
    colorTeamKeyFromSportingColor(sportingColor ?? null) ?? colorTeamKeyFromName(sportingColorName);
  if (!teamKey) {
    return <>{sportingColorName}</>;
  }
  return <TeamBadge saveId={saveId} cup="color" teamKey={teamKey} teamName={sportingColorName} />;
}
