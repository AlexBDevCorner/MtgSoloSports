import { useRef, useState } from 'react';
import { ApiError, apiErrorMessage } from '../../shared/api/http';
import { Notice } from '../../shared/ui/Notice';
import { CatalogCounts } from './CatalogCounts';
import type { CatalogStats } from './catalogApi';
import {
  SCRYFALL_BULK_DOCS_URL,
  formatSourceLabel,
  importFromScryfall,
  needsRefreshConfirmation,
  tryParseQuotaError,
  type ScryfallImportResult,
} from './scryfallImportApi';

type Phase =
  | { kind: 'idle' }
  | { kind: 'confirming' }
  | { kind: 'importing' }
  | { kind: 'success'; result: ScryfallImportResult }
  | { kind: 'error'; message: string; quotaCounts: Record<string, number> | null };

export function ScryfallImportPanel({
  stats,
  onImported,
  idPrefix,
}: {
  stats: CatalogStats | null;
  onImported: () => void;
  idPrefix: string;
}) {
  const [phase, setPhase] = useState<Phase>({ kind: 'idle' });
  const abortRef = useRef<AbortController | null>(null);

  const totalAthletes = stats?.totalAthletes ?? 0;
  const isSufficient = stats?.isSufficientForSave ?? false;
  const populated = needsRefreshConfirmation(totalAthletes);
  const importing = phase.kind === 'importing';
  const confirming = phase.kind === 'confirming';

  function startImport() {
    if (importing) {
      return;
    }
    if (populated && phase.kind === 'idle') {
      setPhase({ kind: 'confirming' });
      return;
    }
    void runImport();
  }

  async function runImport() {
    if (abortRef.current) {
      abortRef.current.abort();
    }
    const controller = new AbortController();
    abortRef.current = controller;
    setPhase({ kind: 'importing' });
    try {
      const result = await importFromScryfall(controller.signal);
      setPhase({ kind: 'success', result });
      onImported();
    } catch (failure) {
      if (failure instanceof DOMException && failure.name === 'AbortError') {
        setPhase({
          kind: 'error',
          message: 'Import was cancelled before saving. The existing catalog was left unchanged. Try again.',
          quotaCounts: null,
        });
        return;
      }
      const quota = tryParseQuotaError(failure);
      if (quota) {
        setPhase({ kind: 'error', message: quota.message, quotaCounts: quota.countsBySportingColor });
        return;
      }
      setPhase({ kind: 'error', message: apiErrorMessage(failure), quotaCounts: null });
    } finally {
      if (abortRef.current === controller) {
        abortRef.current = null;
      }
    }
  }

  function cancelImport() {
    abortRef.current?.abort();
  }

  const statusId = `${idPrefix}-status`;
  const buttonLabel = populated ? 'Refresh catalog from Scryfall' : 'Import cards from Scryfall';

  return (
    <div className="scryfall-import" aria-live="polite">
      <p className="muted small">Card data and images courtesy of Scryfall.</p>
      <details className="advanced">
        <summary>How catalog import works</summary>
        <p className="muted small">
          Cards are downloaded from Scryfall&apos;s MTG bulk data. We keep eligible creature cards,
          exclude tokens and group different printings of the same card into one athlete. Each of
          the eight sporting colors needs 256 athletes before a universe can be created.{' '}
          <a href={SCRYFALL_BULK_DOCS_URL} target="_blank" rel="noreferrer">
            Scryfall bulk-data documentation
          </a>
          . An internet connection is needed for automatic import. Existing saves keep their own
          athlete snapshots and are never modified by catalog updates.
        </p>
      </details>

      {populated ? (
        <p className="muted small">
          Refreshing replaces the <strong>shared catalog only</strong> and does not touch existing
          universes. Refresh only when you intend to replace the shared catalog.
        </p>
      ) : null}

      {confirming ? (
        <Notice tone="warn" title="Replace the shared catalog?">
          <p>
            This downloads a fresh copy from Scryfall and replaces the shared catalog. Existing
            saves stay untouched.
          </p>
          <div className="live-buttons">
            <button
              type="button"
              className="primary-button"
              onClick={() => {
                void runImport();
              }}
            >
              Yes, refresh from Scryfall
            </button>
            <button
              type="button"
              className="ghost-button"
              onClick={() => {
                setPhase({ kind: 'idle' });
              }}
            >
              Keep current catalog
            </button>
          </div>
        </Notice>
      ) : null}

      <div className="live-buttons">
        {phase.kind === 'confirming' ? null : (
          <button
            type="button"
            className="primary-button"
            disabled={importing}
            onClick={startImport}
            aria-describedby={statusId}
          >
            {importing ? 'Importing…' : buttonLabel}
          </button>
        )}
        {importing ? (
          <button type="button" className="ghost-button" onClick={cancelImport}>
            Cancel
          </button>
        ) : null}
        {phase.kind === 'error' || phase.kind === 'success' ? (
          <button
            type="button"
            className="ghost-button"
            disabled={importing}
            onClick={() => {
              setPhase({ kind: 'idle' });
            }}
          >
            Dismiss
          </button>
        ) : null}
      </div>

      <div id={statusId} role="status" aria-live="polite">
        {importing ? (
          <p className="muted small">
            <span className="spinner" aria-hidden="true" /> Contacting Scryfall, downloading the
            advertised bulk file, then verifying quotas before saving. This can take a minute or
            more — please keep this tab open.
          </p>
        ) : null}

        {phase.kind === 'success' ? (
          <SuccessBlock result={phase.result} wasPopulated={populated} isSufficient={isSufficient} />
        ) : null}

        {phase.kind === 'error' ? (
          <Notice tone="error" title="Catalog import did not complete">
            <p>{phase.message}</p>
            {phase.quotaCounts ? (
              <>
                <p className="muted small">Downloaded coverage versus the 256-per-color quota:</p>
                <QuotaBreakdown counts={phase.quotaCounts} />
              </>
            ) : null}
            <div className="live-buttons">
              <button
                type="button"
                className="primary-button"
                disabled={importing}
                onClick={() => {
                  void runImport();
                }}
              >
                Try again
              </button>
            </div>
            <p className="muted small">
              {phase.message.includes('already running')
                ? 'Another import is still running; wait for it to finish.'
                : 'The existing catalog was left unchanged. Check your connection, then retry. Advanced/offline use can still POST a JSON array to /api/catalog/import.'}
            </p>
          </Notice>
        ) : null}
      </div>
    </div>
  );
}

function SuccessBlock({
  result,
  wasPopulated,
  isSufficient,
}: {
  result: ScryfallImportResult;
  wasPopulated: boolean;
  isSufficient: boolean;
}) {
  const sourceLabel = formatSourceLabel(result.sourceName, result.sourceUpdatedAt);
  const ready = result.isSufficientForSave || isSufficient;
  return (
    <Notice tone="empty" title={wasPopulated ? 'Catalog refreshed' : 'Catalog imported'}>
      <p>
        Imported <strong>{result.uniqueAthletes}</strong> unique athletes
        {result.totalPrintings > 0 ? ` from ${result.totalPrintings} bulk entries` : ''}.
        {ready ? ' Ready to create a universe.' : ' Quotas are not met yet — coverage is below.'}
      </p>
      {sourceLabel ? <p className="muted small">{sourceLabel}</p> : null}
      <QuotaBreakdown counts={result.countsBySportingColor} />
    </Notice>
  );
}

function QuotaBreakdown({ counts }: { counts: Record<string, number> }) {
  const synthetic: CatalogStats = {
    totalAthletes: Object.values(counts).reduce((sum, value) => sum + value, 0),
    countsBySportingColor: counts,
    isSufficientForSave: false,
  };
  return <CatalogCounts stats={synthetic} />;
}

export function isConflictError(error: unknown): boolean {
  return error instanceof ApiError && error.status === 409;
}
