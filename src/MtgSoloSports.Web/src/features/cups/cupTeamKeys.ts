import { colorIndex, colorSlug } from '../leagues/leagueKeys';

/**
 * MSS-139 shared Cup team key helpers. Pure and DOM-free.
 * A Color team key is the lower-case sporting-color name; a Type team key is
 * the stored creature type exactly as persisted. Unknown or empty identities
 * return null so callers render plain text instead of a guessed URL.
 */

/** Lower-case Color Cup team key for a numeric sporting color, or null. */
export function colorTeamKeyFromSportingColor(sportingColor: number | null | undefined): string | null {
  return colorSlug(sportingColor);
}

/** Lower-case Color Cup team key for a sporting-color name, or null when unknown. */
export function colorTeamKeyFromName(name: string | null | undefined): string | null {
  if (typeof name !== 'string') {
    return null;
  }
  const slug = name.trim().toLowerCase();
  if (!slug) {
    return null;
  }
  return colorIndex(slug) === null ? null : slug;
}

/**
 * Route-ready team key for a Cup team row. Color teams validate against the
 * eight known sporting colors; Type teams use the stored creature type
 * exactly as persisted (every team in a persisted standing fielded a squad).
 * Returns null when there is no reliable identity.
 */
export function cupTeamKey(
  cup: 'color' | 'type',
  teamName: string | null | undefined,
): string | null {
  if (cup === 'color') {
    return colorTeamKeyFromName(teamName);
  }
  if (typeof teamName !== 'string') {
    return null;
  }
  const trimmed = teamName.trim();
  return trimmed.length > 0 ? trimmed : null;
}
