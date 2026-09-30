import { useCallback, useEffect, useRef, useState, type RefObject } from 'react';

export const RAIL_ID = 'app-rail';

/** Matches the CSS breakpoint above which the rail is always visible. */
const WIDE_LAYOUT_QUERY = '(min-width: 1021px)';

export interface RailDrawer {
  open: boolean;
  toggle: () => void;
  /** Closes after link navigation; focus follows the new page. */
  close: () => void;
  /** Closes on Escape/backdrop; focus returns to the toggle. */
  dismiss: () => void;
  toggleRef: RefObject<HTMLButtonElement | null>;
}

/** Presentation-only drawer state for the narrow layout; never persisted. */
export function useRailDrawer(): RailDrawer {
  const [open, setOpen] = useState(false);
  const toggleRef = useRef<HTMLButtonElement | null>(null);
  const toggle = useCallback(() => {
    setOpen((value) => !value);
  }, []);
  const close = useCallback(() => {
    setOpen(false);
  }, []);
  const dismiss = useCallback(() => {
    setOpen(false);
    toggleRef.current?.focus();
  }, []);

  useEffect(() => {
    if (!open) {
      return;
    }
    document.getElementById(RAIL_ID)?.querySelector<HTMLElement>('a[href]')?.focus();
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        dismiss();
      }
    };
    const wide = window.matchMedia(WIDE_LAYOUT_QUERY);
    const onWide = () => {
      if (wide.matches) {
        setOpen(false);
      }
    };
    window.addEventListener('keydown', onKeyDown);
    wide.addEventListener('change', onWide);
    return () => {
      window.removeEventListener('keydown', onKeyDown);
      wide.removeEventListener('change', onWide);
    };
  }, [open, dismiss]);

  return { open, toggle, close, dismiss, toggleRef };
}
