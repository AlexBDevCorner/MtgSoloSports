import type { DriveLibrarySyncResponse, DriveVerifyResponse, PublishDriveResponse } from './drivePublishingApi';

/** Outcome lists for Publish now, library preview/sync and Verify access. */
export function DriveResultLists({
  publishResult,
  libraryResult,
  verifyResult,
}: {
  publishResult: PublishDriveResponse | null;
  libraryResult: DriveLibrarySyncResponse | null;
  verifyResult: DriveVerifyResponse | null;
}) {
  return (
    <>
      {publishResult ? (
        <ul className="muted small">
          <li>
            Season {publishResult.seasonNumber} · {publishResult.phase}
            {publishResult.globalStage !== null ? ` · stage ${publishResult.globalStage}` : ''}
          </li>
          {publishResult.files.map((file) => (
            <li key={file.report}>
              {file.report}: {file.outcome}
              {file.fileName ? ` — ${file.fileName}` : ''}
              {file.detail ? ` — ${file.detail}` : ''}
            </li>
          ))}
        </ul>
      ) : null}
      {libraryResult ? (
        <ul className="muted small">
          <li>
            Library {libraryResult.dryRun ? 'preview' : 'sync'}: {libraryResult.totalItems} items
            {libraryResult.truncated ? ' (truncated — re-run to resume the remainder)' : ''}
          </li>
          {libraryResult.categories.map((category) => (
            <li key={category.category}>
              {category.category}: {category.total} total · +{category.created} ~{category.updated} =
              {category.unchanged} !{category.failed}
            </li>
          ))}
          {libraryResult.files
            .filter((file) => file.outcome === 'failed')
            .map((file) => (
              <li key={file.report}>
                {file.report}: {file.outcome}
                {file.fileName ? ` — ${file.fileName}` : ''}
                {file.detail ? ` — ${file.detail}` : ''}
              </li>
            ))}
        </ul>
      ) : null}
      {verifyResult ? (
        <ul className="muted small">
          <li>Verify: season {verifyResult.currentSeason}</li>
          {verifyResult.checks.map((check) => (
            <li key={check.report}>
              {check.report}: {check.status}
              {check.fileName ? ` — ${check.fileName}` : ''}
              {check.detail ? ` — ${check.detail}` : ''}
            </li>
          ))}
        </ul>
      ) : null}
    </>
  );
}
