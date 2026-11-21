import { useState, type Dispatch, type SetStateAction } from 'react';
import { mergeLibraryAdoptionEntry } from './drivePublishingApi';
import { DrivePickerButton } from './DrivePickerButton';
import type { DriveSectionControls } from './DrivePublishingCard';

/** Raw library adoption JSON texts owned by the Drive publishing card. */
export interface DriveAdoptionInputs {
  dossierText: string;
  editionText: string;
  teamText: string;
  athleteText: string;
  setDossierText: Dispatch<SetStateAction<string>>;
  setEditionText: Dispatch<SetStateAction<string>>;
  setTeamText: Dispatch<SetStateAction<string>>;
  setAthleteText: Dispatch<SetStateAction<string>>;
}

export function useDriveAdoptions(): DriveAdoptionInputs {
  const [dossierText, setDossierText] = useState('');
  const [editionText, setEditionText] = useState('');
  const [teamText, setTeamText] = useState('');
  const [athleteText, setAthleteText] = useState('');
  return { dossierText, editionText, teamText, athleteText, setDossierText, setEditionText, setTeamText, setAthleteText };
}

export function parseAdoptionMap(
  raw: string,
  label: string,
): Record<string, { fileId: string | null; fileName: string | null }> | null {
  const trimmed = raw.trim();
  if (trimmed === '') {
    return null;
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(trimmed);
  } catch {
    throw new Error(`${label} is not valid JSON. Use {"key": {"fileId": "...", "fileName": "..."}}.`);
  }
  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
    throw new Error(`${label} must be a JSON object keyed by semantic report key.`);
  }
  const entries = Object.entries(parsed as Record<string, unknown>);
  if (entries.length > 500) {
    throw new Error(`${label} has too many entries (max 500).`);
  }
  const normalized: Record<string, { fileId: string | null; fileName: string | null }> = {};
  for (const [key, value] of entries) {
    if (key.trim() === '' || key.includes('/') || key.includes('\\')) {
      throw new Error(`${label} has an invalid key "${key}".`);
    }
    if (typeof value !== 'object' || value === null) {
      throw new Error(`${label} entry "${key}" must be {"fileId": "...", "fileName": "..."}.`);
    }
    const record = value as Record<string, unknown>;
    const fileId = record.fileId;
    const fileName = record.fileName;
    normalized[key.trim()] = {
      fileId: typeof fileId === 'string' && fileId.trim() !== '' ? fileId.trim() : null,
      fileName: typeof fileName === 'string' && fileName.trim() !== '' ? fileName.trim() : null,
    };
  }
  return normalized;
}

/** Library adoption JSON inputs, per-key picker grants and the Save destination button. */
export function DriveAdoptionFields({
  drive,
  adoptions,
  onSaveDestination,
}: {
  drive: DriveSectionControls;
  adoptions: DriveAdoptionInputs;
  onSaveDestination: () => void;
}) {
  const [dossierKey, setDossierKey] = useState('');
  const [editionKey, setEditionKey] = useState('');
  const [teamKey, setTeamKey] = useState('');
  const [athleteKey, setAthleteKey] = useState('');

  return (
    <>
      <div className="toolbar" role="group" aria-label="Drive library file adoptions">
        <label className="field">
          <span>Historical dossier adoptions (JSON)</span>
          <input
            type="text"
            value={adoptions.dossierText}
            disabled={drive.busy !== null}
            placeholder='{"1": {"fileId": "...", "fileName": "season-1-dossier.md"}}'
            onChange={(event) => adoptions.setDossierText(event.target.value)}
          />
        </label>
        <label className="field">
          <span>Cup edition adoptions (JSON)</span>
          <input
            type="text"
            value={adoptions.editionText}
            disabled={drive.busy !== null}
            placeholder='{"color:1": {"fileId": "...", "fileName": "my-edition.md"}}'
            onChange={(event) => adoptions.setEditionText(event.target.value)}
          />
        </label>
        <label className="field">
          <span>Cup team adoptions (JSON)</span>
          <input
            type="text"
            value={adoptions.teamText}
            disabled={drive.busy !== null}
            placeholder='{"color:red": {"fileId": "...", "fileName": "my-team.md"}}'
            onChange={(event) => adoptions.setTeamText(event.target.value)}
          />
        </label>
        <label className="field">
          <span>Athlete adoptions (JSON)</span>
          <input
            type="text"
            value={adoptions.athleteText}
            disabled={drive.busy !== null}
            placeholder='{"123": {"fileId": "...", "fileName": "athlete-123-....md"}}'
            onChange={(event) => adoptions.setAthleteText(event.target.value)}
          />
        </label>
      </div>
      <div className="toolbar" role="group" aria-label="Drive library file grants">
        <label className="field">
          <span>Season key to grant</span>
          <input
            type="text"
            value={dossierKey}
            disabled={drive.busy !== null}
            placeholder="1"
            onChange={(event) => setDossierKey(event.target.value)}
          />
        </label>
        <DrivePickerButton
          drive={drive}
          busyKey="pick-dossier-file"
          label="Choose season file…"
          kind="markdown"
          guard={() =>
            dossierKey.trim() === ''
              ? 'Enter the season key (for example "1") before choosing its Drive file.'
              : null
          }
          onPicked={(picked) => {
            adoptions.setDossierText((current) =>
              mergeLibraryAdoptionEntry(current, dossierKey, picked.id, picked.name),
            );
            drive.setNotice(
              `Granted “${picked.name}” for season ${dossierKey.trim()} — press Save destination, then Verify access.`,
            );
          }}
        />
        <label className="field">
          <span>Edition key to grant</span>
          <input
            type="text"
            value={editionKey}
            disabled={drive.busy !== null}
            placeholder="color:1"
            onChange={(event) => setEditionKey(event.target.value)}
          />
        </label>
        <DrivePickerButton
          drive={drive}
          busyKey="pick-edition-file"
          label="Choose edition file…"
          kind="markdown"
          guard={() =>
            editionKey.trim() === ''
              ? 'Enter the edition key (for example "color:1") before choosing its Drive file.'
              : null
          }
          onPicked={(picked) => {
            adoptions.setEditionText((current) =>
              mergeLibraryAdoptionEntry(current, editionKey, picked.id, picked.name),
            );
            drive.setNotice(
              `Granted “${picked.name}” for edition ${editionKey.trim()} — press Save destination, then Verify access.`,
            );
          }}
        />
        <label className="field">
          <span>Team key to grant</span>
          <input
            type="text"
            value={teamKey}
            disabled={drive.busy !== null}
            placeholder="color:red"
            onChange={(event) => setTeamKey(event.target.value)}
          />
        </label>
        <DrivePickerButton
          drive={drive}
          busyKey="pick-team-file"
          label="Choose team file…"
          kind="markdown"
          guard={() =>
            teamKey.trim() === ''
              ? 'Enter the team key (for example "color:red") before choosing its Drive file.'
              : null
          }
          onPicked={(picked) => {
            adoptions.setTeamText((current) =>
              mergeLibraryAdoptionEntry(current, teamKey, picked.id, picked.name),
            );
            drive.setNotice(
              `Granted “${picked.name}” for team ${teamKey.trim()} — press Save destination, then Verify access.`,
            );
          }}
        />
        <label className="field">
          <span>Athlete id to grant</span>
          <input
            type="text"
            value={athleteKey}
            disabled={drive.busy !== null}
            placeholder="123"
            onChange={(event) => setAthleteKey(event.target.value)}
          />
        </label>
        <DrivePickerButton
          drive={drive}
          busyKey="pick-athlete-file"
          label="Choose athlete file…"
          kind="markdown"
          guard={() =>
            athleteKey.trim() === ''
              ? 'Enter the athlete id (for example "123") before choosing its Drive file.'
              : null
          }
          onPicked={(picked) => {
            adoptions.setAthleteText((current) =>
              mergeLibraryAdoptionEntry(current, athleteKey, picked.id, picked.name),
            );
            drive.setNotice(
              `Granted “${picked.name}” for athlete ${athleteKey.trim()} — press Save destination, then Verify access.`,
            );
          }}
        />
      </div>
      <p className="muted small">
        Adopt hand-uploaded library files by semantic key: dossier seasons as <code>1</code>,{' '}
        <code>2</code>; editions as <code>color:1</code>/<code>type:2</code>/<code>chaos:1</code>; teams as{' '}
        <code>color:red</code>/<code>type:Dragon</code>; athletes as stable ids like <code>123</code>.
        Preview the library to discover keys, enter the key above and grant each file with{' '}
        <em>Choose season/edition/team/athlete file…</em> (Google Picker grants drive.file access to
        that exact file), then Save destination. Adopted files update in place under their custom names;
        unknown files are never renamed or deleted.
      </p>
      <p>
        <button
          type="button"
          className="ghost-button"
          disabled={drive.busy !== null}
          onClick={onSaveDestination}
        >
          {drive.busy === 'save' ? 'Saving…' : 'Save destination'}
        </button>
      </p>
      <p className="muted small">
        Pasting a Drive id alone does not grant access. With the least-privilege <code>drive.file</code>{' '}
        scope the app can only use files it created or that you explicitly granted with{' '}
        <em>Choose from Drive</em> (Google Picker, same Google account). After picking or pasting ids, use{' '}
        <em>Verify access</em> to check each grant; each season keeps its own dossier file (
        <code>Seasons/season-N-dossier.md</code>) so a new season never overwrites the prior season&apos;s
        file. Drive <em>Open with</em> needs a public HTTPS app URL and is not available for localhost.
      </p>
    </>
  );
}
