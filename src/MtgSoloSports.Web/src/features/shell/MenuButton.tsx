import type { RefObject } from 'react';
import { RAIL_ID } from './useRailDrawer';

export function MenuButton({
  open,
  onToggle,
  toggleRef,
}: {
  open: boolean;
  onToggle: () => void;
  toggleRef: RefObject<HTMLButtonElement | null>;
}) {
  return (
    <button
      ref={toggleRef}
      type="button"
      className="menu-button"
      aria-expanded={open}
      aria-controls={RAIL_ID}
      aria-label={open ? 'Close navigation' : 'Open navigation'}
      onClick={onToggle}
    >
      <span className="menu-button-bars" aria-hidden="true" />
    </button>
  );
}
