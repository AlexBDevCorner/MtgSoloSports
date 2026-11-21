import { pickerDisabledHint } from './drivePicker';
import { DrivePickerButton } from './DrivePickerButton';
import type { DriveSectionControls } from './DrivePublishingCard';
import type { DriveDestinationFormState } from './useDriveDestinationForm';

/**
 * Drive destination inputs plus every folder/file picker grant: the root
 * mtshistory, Seasons, dossier and almanac destinations, the Cup library
 * folders and their picker grants.
 */
export function DriveDestinationFields({
  drive,
  form,
  setField,
}: {
  drive: DriveSectionControls;
  form: DriveDestinationFormState;
  setField: (field: keyof DriveDestinationFormState, value: string) => void;
}) {
  return (
    <>
      <div className="toolbar" role="group" aria-label="Drive destination">
        <label className="field">
          <span>mtshistory folder id</span>
          <input
            type="text"
            value={form.mtshistoryFolderId}
            disabled={drive.busy !== null}
            placeholder="Drive folder id for mtshistory"
            onChange={(event) => setField('mtshistoryFolderId', event.target.value)}
          />
        </label>
        <label className="field">
          <span>Folder display name</span>
          <input
            type="text"
            value={form.mtshistoryFolderName}
            disabled={drive.busy !== null}
            placeholder="mtshistory"
            onChange={(event) => setField('mtshistoryFolderName', event.target.value)}
          />
        </label>
        <label className="field">
          <span>Seasons folder id (optional)</span>
          <input
            type="text"
            value={form.seasonsFolderId}
            disabled={drive.busy !== null}
            placeholder="Drive folder id for Seasons/"
            onChange={(event) => setField('seasonsFolderId', event.target.value)}
          />
        </label>
        <label className="field">
          <span>Dossier file id (Seasons/)</span>
          <input
            type="text"
            value={form.dossierFileId}
            disabled={drive.busy !== null}
            placeholder="adopted season dossier id"
            onChange={(event) => setField('dossierFileId', event.target.value)}
          />
        </label>
        <label className="field">
          <span>Dossier file name</span>
          <input
            type="text"
            value={form.dossierFileName}
            disabled={drive.busy !== null}
            placeholder="season-N-dossier.md"
            onChange={(event) => setField('dossierFileName', event.target.value)}
          />
        </label>
        <label className="field">
          <span>Almanac file id (root)</span>
          <input
            type="text"
            value={form.almanacFileId}
            disabled={drive.busy !== null}
            placeholder="adopted almanac id"
            onChange={(event) => setField('almanacFileId', event.target.value)}
          />
        </label>
        <label className="field">
          <span>Almanac file name</span>
          <input
            type="text"
            value={form.almanacFileName}
            disabled={drive.busy !== null}
            placeholder="universe-almanac-….md"
            onChange={(event) => setField('almanacFileName', event.target.value)}
          />
        </label>
      </div>
      <div className="toolbar" role="group" aria-label="Drive Cup library folders">
        <label className="field">
          <span>Color Cup folder id</span>
          <input
            type="text"
            value={form.colorCupFolderId}
            disabled={drive.busy !== null}
            placeholder="Drive folder id for Color Cup/"
            onChange={(event) => setField('colorCupFolderId', event.target.value)}
          />
        </label>
        <label className="field">
          <span>Color Cups folder id</span>
          <input
            type="text"
            value={form.colorCupsFolderId}
            disabled={drive.busy !== null}
            placeholder="Drive folder id for Color Cup/Cups"
            onChange={(event) => setField('colorCupsFolderId', event.target.value)}
          />
        </label>
        <label className="field">
          <span>Color Teams Histories folder id</span>
          <input
            type="text"
            value={form.colorTeamsFolderId}
            disabled={drive.busy !== null}
            placeholder="Drive folder id for Color Cup/Teams Histories"
            onChange={(event) => setField('colorTeamsFolderId', event.target.value)}
          />
        </label>
        <label className="field">
          <span>Type Cup folder id</span>
          <input
            type="text"
            value={form.typeCupFolderId}
            disabled={drive.busy !== null}
            placeholder="Drive folder id for Type Cup/"
            onChange={(event) => setField('typeCupFolderId', event.target.value)}
          />
        </label>
        <label className="field">
          <span>Type Cups folder id</span>
          <input
            type="text"
            value={form.typeCupsFolderId}
            disabled={drive.busy !== null}
            placeholder="Drive folder id for Type Cup/Cups"
            onChange={(event) => setField('typeCupsFolderId', event.target.value)}
          />
        </label>
        <label className="field">
          <span>Type Teams Histories folder id</span>
          <input
            type="text"
            value={form.typeTeamsFolderId}
            disabled={drive.busy !== null}
            placeholder="Drive folder id for Type Cup/Teams Histories"
            onChange={(event) => setField('typeTeamsFolderId', event.target.value)}
          />
        </label>
        <label className="field">
          <span>Chaos Cup folder id</span>
          <input
            type="text"
            value={form.chaosCupFolderId}
            disabled={drive.busy !== null}
            placeholder="Drive folder id for Chaos Cup/"
            onChange={(event) => setField('chaosCupFolderId', event.target.value)}
          />
        </label>
        <label className="field">
          <span>Chaos Cups folder id</span>
          <input
            type="text"
            value={form.chaosCupsFolderId}
            disabled={drive.busy !== null}
            placeholder="Drive folder id for Chaos Cup/Cups"
            onChange={(event) => setField('chaosCupsFolderId', event.target.value)}
          />
        </label>
        <label className="field">
          <span>Athletes folder id</span>
          <input
            type="text"
            value={form.athletesFolderId}
            disabled={drive.busy !== null}
            placeholder="Drive folder id for Athletes/"
            onChange={(event) => setField('athletesFolderId', event.target.value)}
          />
        </label>
        <label className="field">
          <span>Athletes folder name</span>
          <input
            type="text"
            value={form.athletesFolderName}
            disabled={drive.busy !== null}
            placeholder="Athletes (custom root name allowed)"
            onChange={(event) => setField('athletesFolderName', event.target.value)}
          />
        </label>
        <label className="field">
          <span>Athletes index file id</span>
          <input
            type="text"
            value={form.athletesIndexFileId}
            disabled={drive.busy !== null}
            placeholder="Drive file id for athletes-index.md"
            onChange={(event) => setField('athletesIndexFileId', event.target.value)}
          />
        </label>
        <label className="field">
          <span>Athletes index file name</span>
          <input
            type="text"
            value={form.athletesIndexFileName}
            disabled={drive.busy !== null}
            placeholder="athletes-index.md"
            onChange={(event) => setField('athletesIndexFileName', event.target.value)}
          />
        </label>
      </div>
      <div className="toolbar" role="group" aria-label="Drive Picker grant">
        <DrivePickerButton
          drive={drive}
          busyKey="pick-folder"
          label="Choose folder…"
          kind="folder"
          onPicked={(picked) => {
            setField('mtshistoryFolderId', picked.id);
            if (picked.name) {
              setField('mtshistoryFolderName', picked.name);
            }
            drive.setNotice(`Picked folder “${picked.name}” — press Save destination, then Verify access.`);
          }}
        />
        <DrivePickerButton
          drive={drive}
          busyKey="pick-seasons"
          label="Choose Seasons…"
          kind="folder"
          onPicked={(picked) => {
            setField('seasonsFolderId', picked.id);
            drive.setNotice(`Picked Seasons folder “${picked.name}” — press Save destination, then Verify access.`);
          }}
        />
        <DrivePickerButton
          drive={drive}
          busyKey="pick-dossier"
          label="Choose dossier…"
          kind="markdown"
          onPicked={(picked) => {
            setField('dossierFileId', picked.id);
            if (picked.name) {
              setField('dossierFileName', picked.name);
            }
            drive.setNotice(`Picked dossier “${picked.name}” — press Save destination, then Verify access.`);
          }}
        />
        <DrivePickerButton
          drive={drive}
          busyKey="pick-almanac"
          label="Choose almanac…"
          kind="markdown"
          onPicked={(picked) => {
            setField('almanacFileId', picked.id);
            if (picked.name) {
              setField('almanacFileName', picked.name);
            }
            drive.setNotice(`Picked almanac “${picked.name}” — press Save destination, then Verify access.`);
          }}
        />
      </div>
      <div className="toolbar" role="group" aria-label="Drive Cup library folder grants">
        {(
          [
            ['pick-color-cup', 'Choose Color Cup…', 'colorCupFolderId'],
            ['pick-color-cups', 'Choose Color Cups…', 'colorCupsFolderId'],
            ['pick-color-teams', 'Choose Color Teams…', 'colorTeamsFolderId'],
            ['pick-type-cup', 'Choose Type Cup…', 'typeCupFolderId'],
            ['pick-type-cups', 'Choose Type Cups…', 'typeCupsFolderId'],
            ['pick-type-teams', 'Choose Type Teams…', 'typeTeamsFolderId'],
            ['pick-chaos-cup', 'Choose Chaos Cup…', 'chaosCupFolderId'],
            ['pick-chaos-cups', 'Choose Chaos Cups…', 'chaosCupsFolderId'],
          ] as const
        ).map(([busyKey, label, target]) => (
          <DrivePickerButton
            key={target}
            drive={drive}
            busyKey={busyKey}
            label={label}
            kind="folder"
            onPicked={(picked) => {
              setField(target, picked.id);
              drive.setNotice(`Picked folder “${picked.name}” — press Save destination, then Verify access.`);
            }}
          />
        ))}
      </div>
      <p className="muted small">
        {drive.status.pickerEnabled
          ? 'Choose from Drive grants drive.file access to that exact folder/file (same Google account), so publishing can update it in place.'
          : pickerDisabledHint(drive.status)}
      </p>
    </>
  );
}
