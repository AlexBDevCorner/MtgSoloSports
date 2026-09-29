import type { ReactNode } from 'react';

export function Card({
  title,
  eyebrow,
  action,
  children,
  className,
}: {
  title: string;
  eyebrow?: string;
  action?: ReactNode;
  children: ReactNode;
  className?: string;
}) {
  return (
    <section className={className ? `card ${className}` : 'card'} aria-label={title}>
      <header className="card-head">
        <div>
          {eyebrow ? <p className="card-eyebrow">{eyebrow}</p> : null}
          <h2 className="card-title">{title}</h2>
        </div>
        {action ? <div className="card-action">{action}</div> : null}
      </header>
      <div className="card-body">{children}</div>
    </section>
  );
}
