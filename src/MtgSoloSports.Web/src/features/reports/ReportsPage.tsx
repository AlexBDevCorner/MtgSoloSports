import { useEffect, useState } from 'react';
import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import { ApiError, apiErrorMessage, fetchJson } from '../../shared/api/http';
import { downloadReport, seasonDossierReportUrl, universeAlmanacReportUrl } from '../reports/reportApi';
import type { HistorySeasons } from '../history/historyApi';

const JOURNALIST_PROMPT = `You are a longtime MtgSoloSports columnist. Use only facts from the attached season dossier (current season) and universe almanac (history). Distinguish official points (season championship points) from weighted career points; Type Cup qualification never awards titles; Color individual and team events are separate titles; provisional tables are not final. Call out defending champions, streaks, upsets, qualification cut lines, and historical firsts with season numbers. If a fact is not in the sources, say it is unavailable rather than guessing.`;

async function fetchSeasons(saveId: string, signal: AbortSignal): Promise<HistorySeasons> {
  return fetchJson<HistorySeasons>(`/api/saves/${saveId}/history/seasons`, { signal });
}

export function ReportsPage({ saveId }: { saveId: string }) {
  const [seasons, setSeasons] = useState<HistorySeasons | null>(null);
  const [seasonsError, setSeasonsError] = useState<string | null>(null);
  const [seasonsLoading, setSeasonsLoading] = useState(true);
  const [selectedSeason, setSelectedSeason] = useState<number | null>(null);
  const [downloading, setDownloading] = useState<string | null>(null);
  const [downloadError, setDownloadError] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);

  useEffect(() => {
    const controller = new AbortController();
    setSeasonsLoading(true);
    setSeasonsError(null);
    fetchSeasons(saveId, controller.signal)
      .then((loaded) => {
        if (controller.signal.aborted) {
          return;
        }
        const valid = loaded.saveId === saveId ? loaded : null;
        setSeasons(valid);
        const numbers = (valid?.seasons ?? []).map((row) => row.seasonNumber).sort((a, b) => a - b);
        setSelectedSeason(numbers.length > 0 ? numbers[numbers.length - 1]! : null);
        setSeasonsLoading(false);
      })
      .catch((failure: unknown) => {
        if (controller.signal.aborted) {
          return;
        }
        if (failure instanceof ApiError && failure.status === 404) {
          setSeasons(null);
          setSeasonsError(null);
        } else {
          setSeasonsError(apiErrorMessage(failure));
        }
        setSeasonsLoading(false);
      });
    return () => controller.abort();
  }, [saveId]);

  async function handleDownload(url: string, fallback: string, key: string): Promise<void> {
    if (downloading !== null) {
      return;
    }
    setDownloading(key);
    setDownloadError(null);
    try {
      await downloadReport(url, fallback);
    } catch (failure) {
      setDownloadError(apiErrorMessage(failure));
    } finally {
      setDownloading(null);
    }
  }

  async function handleCopyPrompt(): Promise<void> {
    try {
      await navigator.clipboard.writeText(JOURNALIST_PROMPT);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } catch {
      setCopied(false);
    }
  }

  const seasonOptions = (seasons?.seasons ?? []).slice().sort((a, b) => a.seasonNumber - b.seasonNumber);

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

      {seasonsLoading ? <Loading label="Loading seasons…" /> : null}
      {seasonsError ? (
        <Notice tone="error" title="Seasons unavailable">
          <p>{seasonsError}</p>
        </Notice>
      ) : null}

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
              disabled={seasonOptions.length === 0 || downloading !== null}
              onChange={(event) => {
                const next = Number.parseInt(event.target.value, 10);
                setSelectedSeason(Number.isNaN(next) ? null : next);
              }}
            >
              {seasonOptions.map((row) => (
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
              void handleDownload(
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
              void handleDownload(
                seasonDossierReportUrl(saveId, selectedSeason, 'json'),
                `season-${selectedSeason}-dossier.json`,
                `dossier-json-${selectedSeason}`,
              );
            }}
          >
            {downloading?.startsWith('dossier-json-') ? 'Preparing…' : 'Dossier .json'}
          </button>
        </div>
        {selectedSeason === null && !seasonsLoading ? (
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
              void handleDownload(universeAlmanacReportUrl(saveId, 'md'), 'universe-almanac.md', 'almanac-md');
            }}
          >
            {downloading === 'almanac-md' ? 'Preparing…' : 'Almanac .md'}
          </button>
          <button
            type="button"
            className="ghost-button"
            disabled={downloading !== null}
            onClick={() => {
              void handleDownload(universeAlmanacReportUrl(saveId, 'json'), 'universe-almanac.json', 'almanac-json');
            }}
          >
            {downloading === 'almanac-json' ? 'Preparing…' : 'Almanac .json'}
          </button>
        </div>
      </Card>

      {downloadError ? (
        <Notice tone="error" title="Report download failed">
          <p>{downloadError}</p>
        </Notice>
      ) : null}

      <Card eyebrow="Workflow" title="How to brief ChatGPT">
        <ol className="muted">
          <li>Export the almanac occasionally as a stable history reference.</li>
          <li>Export a fresh season dossier whenever you want coverage of a new stage or Cup.</li>
          <li>Attach both files to a ChatGPT Project or a new chat; replace the dossier as the save advances.</li>
        </ol>
        <p className="muted small">
          Reports provide facts and evidence, not prewritten opinions. Scoring note: official points are season
          totals in the dossier tables; weighted career points appear only in the almanac leaders and never
          decide a season title.
        </p>
        <label className="field">
          <span>Example journalist prompt (copy into ChatGPT; no narrative is generated in-app)</span>
          <pre className="muted small" style={{ whiteSpace: 'pre-wrap' }}>
            {JOURNALIST_PROMPT}
          </pre>
        </label>
        <p>
          <button type="button" className="ghost-button" onClick={() => void handleCopyPrompt()}>
            {copied ? 'Copied!' : 'Copy prompt'}
          </button>
        </p>
      </Card>
    </div>
  );
}
