import type { ReactNode } from 'react';

/** Small "i" disclosure for system/implementation notes that should not take page space. */
export function InfoDisclosure({ children }: { children: ReactNode }) {
  return (
    <details className="info">
      <summary aria-label="About this panel" title="About this panel">
        i
      </summary>
      <div className="info-body">{children}</div>
    </details>
  );
}
