// MSS-130: deterministic seed/stream entry must never silently round.
//
// JavaScript numbers are IEEE-754 doubles and can only represent integers
// exactly up to Number.MAX_SAFE_INTEGER (9007199254740991). The backend
// accepts nullable ulong, but the UI transports seed/stream as JSON numbers,
// so any value above the safe range would round (for example
// Number('9007199254740993') becomes 9007199254740992) and silently simulate
// a different universe. We keep numeric JSON and reject unsafe input instead
// of claiming full ulong support.
//
// Both fields share the same documented range:
//   blank (random) or an integer from 0 to 9007199254740991 inclusive.

export const MAX_SAFE_SEED_VALUE = 9007199254740991;

export type DeterministicField = 'Seed' | 'Stream';

export function deterministicFieldError(field: DeterministicField): string {
  return `${field} must be an integer from 0 to ${MAX_SAFE_SEED_VALUE} or blank for random.`;
}

/**
 * Parses a seed/stream textbox value.
 * Returns undefined for blank (caller omits the property so the server draws
 * fresh entropy). Throws a field-related Error for negative, fractional,
 * non-finite, or out-of-range input. Safe decimal integers keep their exact
 * numeric value.
 */
export function parseDeterministicField(raw: string, field: DeterministicField): number | undefined {
  const trimmed = raw.trim();
  if (trimmed === '') {
    return undefined;
  }
  // Canonical decimal digits (optional leading '+'). Anything else — '-', '.',
  // 'e'/'E', 'x', 'Infinity', 'NaN', etc. — is rejected with the field message
  // so exotic Number() forms ('1e3', '0x2A', '1.0') cannot slip through as a
  // silently different value.
  if (!/^\+?\d+$/.test(trimmed)) {
    throw new Error(deterministicFieldError(field));
  }
  const value = Number(trimmed);
  if (!Number.isSafeInteger(value) || value < 0 || value > MAX_SAFE_SEED_VALUE) {
    throw new Error(deterministicFieldError(field));
  }
  return value;
}
