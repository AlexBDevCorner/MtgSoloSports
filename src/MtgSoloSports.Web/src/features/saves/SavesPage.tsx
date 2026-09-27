import { useRef, useState } from 'react';
import { apiErrorMessage } from '../../shared/api/http';
import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import { createSave, exportSaveUrl, importSave, type SaveDetail, type SaveSummary } from './savesApi';
import type { SavesState } from './useSaves';

function toSaveSummary(result: {
  saveId: string;
  name: string;
  createdUtc: string;
  schemaVersion: number;
  currentSeason: number;
  phase: string;
}): SaveSummary {
  return {
    saveId: result.saveId,
    name: result.name,
    createdUtc: result.createdUtc,
    schemaVersion: result.schemaVersion,
    currentSeason: result.currentSeason,
    phase: result.phase,
  };
}

export function SavesPage({
  saves,
  selectedSaveId,
  onSelect,
  catalogSufficient,
}: {
  saves: SavesState;
  selectedSaveId: string | null;
  onSelect: (saveId: string) => void;
  catalogSufficient: boolean;
}) {
  const [name, setName] = useState('');
  const [seed, setSeed] = useState('');
  const [stream, setStream] = useState('');
  const [creating, setCreating] = useState(false);
  const [createError, setCreateError] = useState<string | null>(null);
  const [importFile, setImportFile] = useState<File | null>(null);
  const [overwrite, setOverwrite] = useState(false);
  const [importing, setImporting] = useState(false);
  const [importError, setImportError] = useState<string | null>(null);
  const [importedName, setImportedName] = useState<string | null>(null);
  const fileInputRef = useRef<HTMLInputElement | null>(null);

  async function handleCreate(event: React.FormEvent) {
    event.preventDefault();
    const trimmed = name.trim();
    if (!trimmed || creating) {
      return;
    }
    setCreating(true);
    setCreateError(null);
    try {
      const parsedSeed = seed.trim() === '' ? undefined : Number(seed);
      const parsedStream = stream.trim() === '' ? undefined : Number(stream);
      if (
        (parsedSeed !== undefined && (!Number.isInteger(parsedSeed) || parsedSeed < 0)) ||
        (parsedStream !== undefined && (!Number.isInteger(parsedStream) || parsedStream < 0))
      ) {
        throw new Error('Seed and stream must be non-negative integers when provided.');
      }
      const created = await createSave(trimmed, parsedSeed, parsedStream);
      saves.addCreated(toSaveSummary(created));
      setName('');
      setSeed('');
      setStream('');
      onSelect(created.saveId);
    } catch (failure) {
      setCreateError(apiErrorMessage(failure));
    } finally {
      setCreating(false);
    }
  }

  async function handleImport(event: React.FormEvent) {
    event.preventDefault();
    if (!importFile || importing) {
      return;
    }
    setImporting(true);
    setImportError(null);
    setImportedName(null);
    try {
      const imported: SaveDetail = await importSave(importFile, overwrite);
      saves.addCreated(toSaveSummary(imported));
      setImportedName(imported.name);
      setImportFile(null);
      setOverwrite(false);
      if (fileInputRef.current) {
        fileInputRef.current.value = '';
      }
      onSelect(imported.saveId);
    } catch (failure) {
      setImportError(apiErrorMessage(failure));
    } finally {
      setImporting(false);
    }
  }

  return (
    <div className="page-grid">
      <Card eyebrow="Saves" title="Select a universe" action={
        <button type="button" className="ghost-button" onClick={saves.refresh} disabled={saves.loading}>
          Refresh
        </button>
      }>
        {saves.loading ? (
          <Loading label="Loading saves…" />
        ) : saves.error ? (
          <Notice tone="error" title="Could not load saves">
            <p>{saves.error}</p>
          </Notice>
        ) : saves.saves.length === 0 ? (
          <Notice tone="empty" title="No saves yet">
            <p>Create your first universe below. Each save is an independent 2,048-athlete world.</p>
          </Notice>
        ) : (
          <ul className="save-list">
            {saves.saves.map((save) => {
              const active = save.saveId === selectedSaveId;
              const deleting = saves.deletingId === save.saveId;
              return (
                <li key={save.saveId} className={active ? 'save-row active' : 'save-row'}>
                  <button
                    type="button"
                    className="save-open"
                    onClick={() => {
                      onSelect(save.saveId);
                    }}
                    aria-current={active ? 'true' : undefined}
                  >
                    <span className="save-name">{save.name}</span>
                    <span className="save-meta">
                      Season {save.currentSeason} · {save.phase} ·{' '}
                      {new Date(save.createdUtc).toLocaleDateString()}
                    </span>
                  </button>
                  <span className="save-row-actions">
                    {active ? <span className="badge">Open</span> : null}
                    <a className="ghost-button" href={exportSaveUrl(save.saveId)} download>
                      Export
                    </a>
                    <button
                      type="button"
                      className="danger-link"
                      disabled={deleting}
                      onClick={() => {
                        void saves.remove(save.saveId).catch(() => {
                          // remove() already filters on success; list refresh surfaces failures
                        });
                      }}
                    >
                      {deleting ? 'Deleting…' : 'Delete'}
                    </button>
                  </span>
                </li>
              );
            })}
          </ul>
        )}
      </Card>

      <Card eyebrow="New save" title="Create a universe">
        {!catalogSufficient ? (
          <Notice tone="warn" title="Catalog quota not met">
            <p>Creation is disabled until every sporting color has 256 catalog athletes.</p>
          </Notice>
        ) : null}
        <form className="form" onSubmit={(event) => void handleCreate(event)}>
          <label className="field">
            <span>Name</span>
            <input
              value={name}
              onChange={(event) => {
                setName(event.target.value);
              }}
              placeholder="My sporting universe"
              maxLength={120}
              required
            />
          </label>
          <details className="advanced">
            <summary>Advanced: deterministic seed (optional)</summary>
            <p className="muted small">
              Leave blank for a random universe. Provide both values only to reproduce an
              exact universe for testing.
            </p>
            <div className="field-row">
              <label className="field">
                <span>Seed</span>
                <input
                  value={seed}
                  onChange={(event) => {
                    setSeed(event.target.value);
                  }}
                  inputMode="numeric"
                  placeholder="Blank = random"
                />
              </label>
              <label className="field">
                <span>Stream</span>
                <input
                  value={stream}
                  onChange={(event) => {
                    setStream(event.target.value);
                  }}
                  inputMode="numeric"
                  placeholder="Blank = random"
                />
              </label>
            </div>
          </details>
          {createError ? (
            <Notice tone="error" title="Could not create save">
              <p>{createError}</p>
            </Notice>
          ) : null}
          <button
            type="submit"
            className="primary-button"
            disabled={creating || name.trim() === '' || !catalogSufficient}
          >
            {creating ? 'Creating…' : 'Create save'}
          </button>
        </form>
      </Card>

      <Card eyebrow="Portability" title="Import a save">
        <form className="form" onSubmit={(event) => void handleImport(event)}>
          <label className="field">
            <span>Save bundle (.mtgsave.zip)</span>
            <input
              ref={fileInputRef}
              type="file"
              accept=".zip,application/zip"
              onChange={(event) => {
                setImportFile(event.target.files?.[0] ?? null);
                setImportedName(null);
              }}
              required
            />
          </label>
          <label className="check-row">
            <input
              type="checkbox"
              checked={overwrite}
              onChange={(event) => {
                setOverwrite(event.target.checked);
              }}
            />
            <span>Replace the existing save with the same id (creates a verified recovery checkpoint first)</span>
          </label>
          {importError ? (
            <Notice tone="error" title="Could not import save">
              <p>{importError}</p>
            </Notice>
          ) : null}
          {importedName ? (
            <Notice tone="empty" title="Import complete">
              <p>Imported “{importedName}”. Incompatible or corrupt bundles are rejected without touching existing saves.</p>
            </Notice>
          ) : null}
          <button type="submit" className="primary-button" disabled={importing || !importFile}>
            {importing ? 'Importing…' : 'Import save'}
          </button>
        </form>
      </Card>
    </div>
  );
}
