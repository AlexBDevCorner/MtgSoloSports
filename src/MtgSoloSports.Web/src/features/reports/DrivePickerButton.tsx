import { apiErrorMessage } from '../../shared/api/http';
import { pickerConfigFromStatus, pickerDisabledHint, showDrivePicker, type DrivePickerSelection } from './drivePicker';
import type { DriveSectionControls } from './DrivePublishingCard';

/**
 * The one Google Drive picker button. Every picker instance on the reports
 * page (folder/seasons/dossier/almanac grants, cup library folders and
 * library file adoptions) uses this control, so the config guard, busy
 * gating, error surface and .then/.catch/.finally chain exist exactly once.
 * Per-instance behavior is only busyKey, picker kind, an optional guard
 * message and what happens with the picked selection.
 */
export function DrivePickerButton({
  drive,
  busyKey,
  label,
  kind,
  guard,
  onPicked,
}: {
  drive: DriveSectionControls;
  busyKey: string;
  label: string;
  kind: 'folder' | 'markdown';
  /** Optional pre-flight validation; returns the error message that should block opening the picker. */
  guard?: () => string | null;
  /** Applies the picked Drive selection; a throw is surfaced as the picker error. */
  onPicked: (picked: DrivePickerSelection) => void;
}) {
  return (
    <button
      type="button"
      className="ghost-button"
      disabled={drive.busy !== null || pickerConfigFromStatus(drive.status) === null}
      title={pickerDisabledHint(drive.status)}
      onClick={() => {
        const config = pickerConfigFromStatus(drive.status);
        if (!config) {
          drive.setError(pickerDisabledHint(drive.status));
          return;
        }
        const blocked = guard?.() ?? null;
        if (blocked !== null) {
          drive.setError(blocked);
          return;
        }
        drive.setBusy(busyKey);
        drive.setError(null);
        void showDrivePicker(config, kind)
          .then((picked) => {
            if (picked) {
              try {
                onPicked(picked);
              } catch (failure) {
                drive.setError(failure instanceof Error ? failure.message : 'Could not adopt the picked file.');
              }
            }
          })
          .catch((failure: unknown) => drive.setError(apiErrorMessage(failure)))
          .finally(() => drive.setBusy(null));
      }}
    >
      {drive.busy === busyKey ? 'Opening Picker…' : label}
    </button>
  );
}
