import type { DriveLibrarySyncResponse } from './drivePublishingApi';

/** One page of a Drive library sync, keyed by the backend paging contract. */
export type DriveLibrarySyncPage = (options: {
  confirmCrossSave: boolean;
  allowOverwriteEditedRemote: boolean;
  allowCreateMissing?: boolean;
  skip?: number;
}) => Promise<DriveLibrarySyncResponse>;

export function mergeLibraryCategories(
  left: DriveLibrarySyncResponse['categories'],
  right: DriveLibrarySyncResponse['categories'],
): DriveLibrarySyncResponse['categories'] {
  const merged = new Map<string, DriveLibrarySyncResponse['categories'][number]>();
  for (const entry of [...left, ...right]) {
    const existing = merged.get(entry.category);
    merged.set(
      entry.category,
      existing === undefined
        ? entry
        : {
            category: entry.category,
            total: existing.total + entry.total,
            created: existing.created + entry.created,
            updated: existing.updated + entry.updated,
            unchanged: existing.unchanged + entry.unchanged,
            failed: existing.failed + entry.failed,
          },
    );
  }
  return [...merged.values()];
}

/**
 * Pages through a Drive library sync until the backend stops truncating
 * (max 20 pages), merging every page into one aggregated result. Shared by
 * the report library and athlete archive sync buttons; the caller only
 * supplies which sync endpoint each page hits.
 */
export async function aggregateLibrarySync(
  syncPage: DriveLibrarySyncPage,
  options: { confirmCrossSave: boolean; allowOverwriteEditedRemote: boolean; allowCreateMissing?: boolean },
): Promise<DriveLibrarySyncResponse | null> {
  let skip: number | undefined;
  let aggregated: DriveLibrarySyncResponse | null = null;
  for (let page = 0; page < 20; page += 1) {
    const result = await syncPage({ ...options, ...(skip === undefined ? {} : { skip }) });
    aggregated =
      aggregated === null
        ? result
        : {
            ...result,
            totalItems: result.totalItems,
            truncated: result.truncated,
            nextSkip: result.nextSkip,
            dryRun: false,
            files: [...aggregated.files, ...result.files],
            categories: mergeLibraryCategories(aggregated.categories, result.categories),
          };
    if (!result.truncated || result.nextSkip === null || result.nextSkip === undefined) {
      if (aggregated !== null) {
        aggregated = { ...aggregated, truncated: false, nextSkip: null };
      }
      break;
    }
    skip = result.nextSkip;
  }
  return aggregated;
}
