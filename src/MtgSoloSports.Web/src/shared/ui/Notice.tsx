import type { ReactNode } from 'react';

type NoticeTone = 'info' | 'warn' | 'error' | 'empty';

export function Notice({
  tone = 'info',
  title,
  children,
}: {
  tone?: NoticeTone;
  title: string;
  children?: ReactNode;
}) {
  return (
    <div className={`notice notice-${tone}`} role="status">
      <p className="notice-title">{title}</p>
      {children ? <div className="notice-body">{children}</div> : null}
    </div>
  );
}

export function Loading({ label }: { label: string }) {
  return (
    <div className="loading" role="status" aria-live="polite">
      <span className="spinner" aria-hidden="true" />
      <span>{label}</span>
    </div>
  );
}
