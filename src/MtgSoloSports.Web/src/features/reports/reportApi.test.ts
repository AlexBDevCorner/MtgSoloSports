import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { athleteReportUrl, downloadReport, standingsReportUrl } from './reportApi';

describe('MSS-086 report downloads', () => {
  it('builds athlete report URLs for both formats', () => {
    assert.equal(athleteReportUrl('save-1', 42, 'md'), '/api/saves/save-1/athletes/42/report?format=md');
    assert.equal(athleteReportUrl('save-1', 42, 'json'), '/api/saves/save-1/athletes/42/report?format=json');
  });

  it('builds standings report URLs with exact season and league scope', () => {
    assert.equal(
      standingsReportUrl('save-1', 7, 3, 'json'),
      '/api/saves/save-1/leagues/7/seasons/3/report?format=json',
    );
    assert.equal(
      standingsReportUrl('save-1', 7, 3, 'md'),
      '/api/saves/save-1/leagues/7/seasons/3/report?format=md',
    );
  });

  it('downloads a blob and honors the backend filename', async () => {
    const seen: string[] = [];
    const g = globalThis as unknown as Record<string, unknown>;
    const prevFetch = g['fetch'];
    const prevDocument = g['document'];
    const prevUrl = g['URL'];
    try {
      const bytes = new TextEncoder().encode('# Standings');
      g['fetch'] = () =>
        Promise.resolve({
          ok: true,
          status: 200,
          headers: {
            get: (name: string) =>
              name.toLowerCase() === 'content-disposition'
                ? 'attachment; filename="standings-season-3-league-7-report.md"; filename*=UTF-8\'\'standings-season-3-league-7-report.md'
                : null,
          },
          blob: () => Promise.resolve(new Blob([bytes], { type: 'text/markdown' })),
          text: () => Promise.resolve('# Standings'),
        });
      const clicked: Array<{ download: string }> = [];
      g['document'] = {
        createElement: () => ({
          set href(_value: string) {},
          set download(value: string) {
            seen.push(value);
          },
          click: () => {
            clicked.push({ download: seen[seen.length - 1] ?? '' });
          },
          remove: () => undefined,
        }),
        body: { appendChild: () => undefined },
      };
      g['URL'] = {
        createObjectURL: () => 'blob:report',
        revokeObjectURL: () => undefined,
      };
      await downloadReport('/api/saves/s/reports?format=md', 'fallback.md');
      assert.equal(seen[0], 'standings-season-3-league-7-report.md');
      assert.equal(clicked.length, 1);
    } finally {
      if (prevFetch === undefined) {
        delete g['fetch'];
      } else {
        g['fetch'] = prevFetch;
      }
      if (prevDocument === undefined) {
        delete g['document'];
      } else {
        g['document'] = prevDocument;
      }
      if (prevUrl === undefined) {
        delete g['URL'];
      } else {
        g['URL'] = prevUrl;
      }
    }
  });

  it('surfaces server errors without downloading', async () => {
    const g = globalThis as unknown as Record<string, unknown>;
    const prevFetch = g['fetch'];
    try {
      g['fetch'] = () =>
        Promise.resolve({
          ok: false,
          status: 404,
          headers: { get: () => null },
          blob: () => Promise.resolve(new Blob([])),
          text: () => Promise.resolve(JSON.stringify({ error: 'League 9 is not part of season 3.' })),
        });
      await assert.rejects(
        () => downloadReport('/api/saves/s/leagues/9/seasons/3/report?format=json', 'fallback.json'),
        /League 9 is not part of season 3/,
      );
    } finally {
      if (prevFetch === undefined) {
        delete g['fetch'];
      } else {
        g['fetch'] = prevFetch;
      }
    }
  });
});
