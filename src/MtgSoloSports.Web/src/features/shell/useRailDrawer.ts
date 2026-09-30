import { useCallback, useEffect, useState } from 'react';

export const RAIL_ID = 'app-rail';

export interface RailDrawer {
  open: boolean;
  toggle: () => void;
  close: () => void;
}

/** Presentation-only drawer state for the narrow layout; never persisted. */
export function useRailDrawer(): RailDrawer {
  const [open, setOpen] = useState(false);
  const toggle = useCallback(() => {
    setOpen((value) => !value);
  }, []);
  const close = useCallback(() => {
    setOpen(false);
  }, []);

  useEffect(() => {
    if (!open) {
      return;
    }
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setOpen(false);
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => {
      window.removeEventListener('keydown', onKeyDown);
    };
  }, [open]);

  return { open, toggle, close };
}
