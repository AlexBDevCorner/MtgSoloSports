import type { ReactNode } from 'react';
import { InfoDisclosure } from './InfoDisclosure';

export function Card({
  title,
  eyebrow,
  action,
  info,
  children,
  className,
}: {
  title: string;
  eyebrow?: string;
  action?: ReactNode;
  /** System notes shown behind an "i" disclosure instead of inline prose. */
  info?: ReactNode;
  children: ReactNode;
  className?: string;
}) {
  return (
    <section className={className ? `card ${className}` : 'card'} aria-label={title}>
      <header className="card-head">
        <div className="card-heading">
          {eyebrow ? <p className="card-eyebrow">{eyebrow}</p> : null}
          <h2 className="card-title">{title}</h2>
        </div>
        {action || info ? (
          <div className="card-action">
            {action}
            {info ? <InfoDisclosure>{info}</InfoDisclosure> : null}
          </div>
        ) : null}
      </header>
      <div className="card-body">{children}</div>
    </section>
  );
}
