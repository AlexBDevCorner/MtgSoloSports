import { useCallback, useEffect, useState, type MouseEvent, type ReactNode } from 'react';
import { athletePath, parseRoute, type Route } from './routes';

export const SELECTED_SAVE_KEY = 'mtg-solo-sports:selected-save';

export function readLastSelectedSave(): string | null {
  try {
    const value = localStorage.getItem(SELECTED_SAVE_KEY);
    return value && value.length > 0 ? value : null;
  } catch {
    return null;
  }
}

export function writeLastSelectedSave(saveId: string): void {
  try {
    localStorage.setItem(SELECTED_SAVE_KEY, saveId);
  } catch {
    // storage is best-effort; routing still works in memory
  }
}

export function removeLastSelectedSave(): void {
  try {
    localStorage.removeItem(SELECTED_SAVE_KEY);
  } catch {
    // ignore
  }
}

export function getCurrentRoute(): Route {
  return parseRoute(window.location.pathname, window.location.search);
}

export function navigate(to: string, options?: { replace?: boolean }): void {
  if (options?.replace) {
    window.history.replaceState(null, '', to);
  } else {
    window.history.pushState(null, '', to);
  }
  window.dispatchEvent(new PopStateEvent('popstate'));
}

export function useBrowserRoute(): Route {
  const [route, setRoute] = useState<Route>(() => getCurrentRoute());

  useEffect(() => {
    const onPopState = () => {
      setRoute(getCurrentRoute());
    };
    window.addEventListener('popstate', onPopState);
    return () => {
      window.removeEventListener('popstate', onPopState);
    };
  }, []);

  return route;
}

export function useNavigate(): (to: string, options?: { replace?: boolean }) => void {
  return useCallback((to: string, options?: { replace?: boolean }) => {
    navigate(to, options);
  }, []);
}

function shouldInterceptClick(event: MouseEvent<HTMLAnchorElement>): boolean {
  // Preserve native browser behavior for Ctrl/Cmd-click, middle-click,
  // Shift-click and context-menu interactions so profiles and screens can
  // open side by side in separate tabs.
  if (event.defaultPrevented) {
    return false;
  }
  if (event.button !== 0) {
    return false;
  }
  if (event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) {
    return false;
  }
  const target = event.currentTarget.target;
  if (target && target !== '_self') {
    return false;
  }
  return true;
}

/**
 * Semantic anchor for in-app navigation. Renders a real `<a href>` so
 * copy-link, open-in-new-tab, middle-click and context menus work natively;
 * plain left-click is intercepted for SPA pushState without a reload.
 */
export function Link({
  to,
  replace,
  className,
  title,
  ariaCurrent,
  ariaLabel,
  children,
}: {
  to: string;
  replace?: boolean;
  className?: string;
  title?: string;
  ariaCurrent?: 'page' | undefined;
  ariaLabel?: string;
  children: ReactNode;
}) {
  return (
    <a
      href={to}
      className={className}
      title={title}
      aria-current={ariaCurrent}
      aria-label={ariaLabel}
      onClick={(event) => {
        if (!shouldInterceptClick(event)) {
          return;
        }
        event.preventDefault();
        navigate(to, { replace });
      }}
    >
      {children}
    </a>
  );
}

/** Athlete profile link for the correct athlete in the correct save. */
export function AthleteLink({
  saveId,
  athleteId,
  name,
  className,
  title,
  children,
}: {
  saveId: string;
  athleteId: number;
  name?: string;
  className?: string;
  title?: string;
  children?: ReactNode;
}) {
  const label = children ?? name ?? `Athlete ${athleteId}`;
  return (
    <Link
      to={athletePath(saveId, athleteId)}
      className={className ?? 'card-name card-link'}
      title={title ?? (name ? `Open career profile for ${name}` : `Open career profile`)}
    >
      {label}
    </Link>
  );
}
