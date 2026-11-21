import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import { DrivePublishingCard } from './DrivePublishingCard';
import { ReportDownloadCards } from './ReportDownloadCards';
import { useReportDownloads } from './useReportDownloads';
import { WorkflowCard } from './WorkflowCard';

/** Reports page: thin composition of the downloads, Drive publishing and workflow sections. */
export function ReportsPage({ saveId }: { saveId: string }) {
  const downloads = useReportDownloads(saveId);

  return (
    <div className="dashboard reports-page">
      <Card eyebrow="Export data" title="Reports for ChatGPT">
        <p>
          Download a season dossier for one season and the universe almanac for all-time reference. Both are
          versioned source packs (Markdown for reading, JSON for analysis) using the same contracts as the
          athlete, standings, Cup and league reports.
        </p>
        <p className="muted small">
          Attach the files to a ChatGPT Project or chat. This app never syncs projects automatically; uploads
          become stale as simulation advances, so re-export the almanac occasionally and a fresh dossier for new
          coverage.
        </p>
      </Card>

      {downloads.seasonsLoading ? <Loading label="Loading seasons…" /> : null}
      {downloads.seasonsError ? (
        <Notice tone="error" title="Seasons unavailable">
          <p>{downloads.seasonsError}</p>
        </Notice>
      ) : null}

      <ReportDownloadCards saveId={saveId} downloads={downloads} />
      <DrivePublishingCard saveId={saveId} />

      {downloads.downloadError ? (
        <Notice tone="error" title="Report download failed">
          <p>{downloads.downloadError}</p>
        </Notice>
      ) : null}

      <WorkflowCard />
    </div>
  );
}
