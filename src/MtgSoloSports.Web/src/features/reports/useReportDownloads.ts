import { useEffect, useState } from 'react';
import { ApiError, apiErrorMessage } from '../../shared/api/http';
import { fetchHistorySeasons, type HistorySeason, type HistorySeasons } from '../history/historyApi';
import { downloadReport } from './reportApi';

export interface ReportDownloads {
  seasonsLoading: boolean;
  seasonsError: string | null;
  seasonOptions: HistorySeason[];
  selectedSeason: number | null;
  selectSeason: (season: number | null) => void;
  downloading: string | null;
  downloadError: string | null;
  download: (url: string, fallback: string, key: string) => Promise<void>;
}

/** Seasons selection plus report download state for the reports page. */
export function useReportDownloads(saveId: string): ReportDownloads {
  const [seasons, setSeasons] = useState<HistorySeasons | null>(null);
  const [seasonsError, setSeasonsError] = useState<string | null>(null);
  const [seasonsLoading, setSeasonsLoading] = useState(true);
  const [selectedSeason, setSelectedSeason] = useState<number | null>(null);
  const [downloading, setDownloading] = useState<string | null>(null);
  const [downloadError, setDownloadError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    setSeasonsLoading(true);
    setSeasonsError(null);
    fetchHistorySeasons(saveId, controller.signal)
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

  async function download(url: string, fallback: string, key: string): Promise<void> {
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

  const seasonOptions = (seasons?.seasons ?? []).slice().sort((a, b) => a.seasonNumber - b.seasonNumber);

  return {
    seasonsLoading,
    seasonsError,
    seasonOptions,
    selectedSeason,
    selectSeason: setSelectedSeason,
    downloading,
    downloadError,
    download,
  };
}
