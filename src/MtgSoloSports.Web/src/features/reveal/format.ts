/** Display-only projections of fixed-point thousandths values (no sporting math). */
export function formatPoints(thousandths: number): string {
  return (thousandths / 1000).toFixed(3);
}

/** Display-only projection of a fixed-point bonus (no sporting math). */
export function formatBonus(thousandths: number): string {
  const sign = thousandths >= 0 ? '+' : '';
  return `${sign}${(thousandths / 1000).toFixed(3)}`;
}

export function formatMovement(movement: number): string {
  if (movement > 0) {
    return `+${movement}`;
  }
  return `${movement}`;
}

export function cardCaption(setCode: string | null, typeLine: string): string | null {
  const parts = [setCode, typeLine].filter(
    (part): part is string => typeof part === 'string' && part.length > 0,
  );
  return parts.length > 0 ? parts.join(' · ') : null;
}
