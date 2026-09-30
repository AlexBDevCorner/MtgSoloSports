import { RAIL_ID } from './useRailDrawer';

export function MenuButton({ open, onToggle }: { open: boolean; onToggle: () => void }) {
  return (
    <button
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
