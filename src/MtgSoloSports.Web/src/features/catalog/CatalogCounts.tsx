import type { CatalogStats } from './catalogApi';
import { COLOR_ORDER, countForColor, REQUIRED_PER_COLOR } from './scryfallImportApi';

export function CatalogCounts({ stats }: { stats: CatalogStats }) {
  return (
    <ul className="color-counts">
      {COLOR_ORDER.map((color) => {
        const count = countForColor(stats.countsBySportingColor, color);
        const ok = count >= REQUIRED_PER_COLOR;
        return (
          <li key={color} className={ok ? 'ok' : 'short'}>
            <span>{color}</span>
            <strong>
              {count}/{REQUIRED_PER_COLOR}
            </strong>
          </li>
        );
      })}
    </ul>
  );
}
