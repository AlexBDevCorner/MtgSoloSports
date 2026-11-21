import { Card } from '../../shared/ui/Card';
import { seasonDossierReportUrl, universeAlmanacReportUrl } from './reportApi';
import type { ReportDownloads } from './useReportDownloads';

/** Season dossier and universe almanac download cards. */
export function ReportDownloadCards({ saveId, downloads }: { saveId: string; downloads: ReportDownloads }) {
  const { selectedSeason, selectSeason, downloading, download } = downloads;

  return (
    <>
      <Card eyebrow="Season dossier" title="One season, journalism-ready">
        <p className="muted small">
          Full Superleague table, stage-by-stage winners, feeder summaries, movement/qualifier status, completed
          Cup summaries (Type qualification vs Final and Color individual vs team kept separate), defending
          champions, leader/Cup-performer career titles and official career records. Provisional and
          not-yet-held sections are labelled explicitly and never inferred.
        </p>
        <div className="toolbar" role="group" aria-label="Season dossier downloads">
          <label className="field">
            <span>Season</span>
            <select
              value={selectedSeason ?? ''}
              disabled={downloads.seasonOptions.length === 0 || downloading !== null}
              onChange={(event) => {
                const next = Number.parseInt(event.target.value, 10);
                selectSeason(Number.isNaN(next) ? null : next);
              }}
            >
              {downloads.seasonOptions.map((row) => (
                <option key={row.seasonNumber} value={row.seasonNumber}>
                  Season {row.seasonNumber}
                  {row.isComplete ? '' : ' (in progress)'}
                </option>
              ))}
            </select>
          </label>
          <button
            type="button"
            className="ghost-button"
            disabled={selectedSeason === null || downloading !== null}
            onClick={() => {
              if (selectedSeason === null) {
                return;
              }
              void download(
                seasonDossierReportUrl(saveId, selectedSeason, 'md'),
                `season-${selectedSeason}-dossier.md`,
                `dossier-md-${selectedSeason}`,
              );
            }}
          >
            {downloading?.startsWith('dossier-md-') ? 'Preparing…' : 'Dossier .md'}
          </button>
          <button
            type="button"
            className="ghost-button"
            disabled={selectedSeason === null || downloading !== null}
            onClick={() => {
              if (selectedSeason === null) {
                return;
              }
              void download(
                seasonDossierReportUrl(saveId, selectedSeason, 'json'),
                `season-${selectedSeason}-dossier.json`,
                `dossier-json-${selectedSeason}`,
              );
            }}
          >
            {downloading?.startsWith('dossier-json-') ? 'Preparing…' : 'Dossier .json'}
          </button>
        </div>
        {selectedSeason === null && !downloads.seasonsLoading ? (
          <p className="muted small">No seasons yet to export.</p>
        ) : null}
      </Card>

      <Card eyebrow="Universe almanac" title="All-time reference">
        <p className="muted small">
          Season champions and podiums, Color/Type Cup medal history, official records, all-time Career Leaders
          with labelled weighted career points plus Superleague points leaders as a separate statistic, and
          consecutive title streaks. Covers active and inactive athletes.
        </p>
        <div className="toolbar" role="group" aria-label="Almanac downloads">
          <button
            type="button"
            className="ghost-button"
            disabled={downloading !== null}
            onClick={() => {
              void download(universeAlmanacReportUrl(saveId, 'md'), 'universe-almanac.md', 'almanac-md');
            }}
          >
            {downloading === 'almanac-md' ? 'Preparing…' : 'Almanac .md'}
          </button>
          <button
            type="button"
            className="ghost-button"
            disabled={downloading !== null}
            onClick={() => {
              void download(universeAlmanacReportUrl(saveId, 'json'), 'universe-almanac.json', 'almanac-json');
            }}
          >
            {downloading === 'almanac-json' ? 'Preparing…' : 'Almanac .json'}
          </button>
        </div>
      </Card>
    </>
  );
}
