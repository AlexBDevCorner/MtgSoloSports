/**
 * Shared display-only projections of fixed-point values (MSS-154). No
 * sporting math: points stay fixed-point integers everywhere else and are
 * divided by 1000 only at the presentation boundary, exactly once here.
 */

/** Display-only projection of a fixed-point thousandths value. */
export function formatPoints(thousandths: number): string {
  return (thousandths / 1000).toFixed(3);
}
