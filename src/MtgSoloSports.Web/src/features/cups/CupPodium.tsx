import type { ReactNode } from 'react';
import { ordinal } from './cupFormat';
import './CupHistory.css';

export interface PodiumEntry {
  key: string;
  /** 1, 2 or 3. */
  place: number;
  art: ReactNode;
  label: ReactNode;
  detail: string;
}

/** The top three of a Cup event, first place first. */
export function CupPodium({ entries }: { entries: PodiumEntry[] }) {
  const ordered = [...entries].sort((a, b) => a.place - b.place);
  return (
    <ol className="cup-podium">
      {ordered.map((entry) => (
        <li key={entry.key} className={entry.place === 1 ? 'cup-podium-step is-first' : 'cup-podium-step'}>
          <span className="cup-podium-place">{ordinal(entry.place)}</span>
          {entry.art}
          <span>{entry.label}</span>
          <span className="cup-podium-detail">{entry.detail}</span>
        </li>
      ))}
    </ol>
  );
}
