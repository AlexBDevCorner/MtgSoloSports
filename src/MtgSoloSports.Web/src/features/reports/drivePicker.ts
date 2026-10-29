import type { DriveStatus } from './drivePublishingApi';

export interface DrivePickerSelection {
  id: string;
  name: string;
  mimeType: string | null;
}

export interface DrivePickerConfig {
  clientId: string;
  apiKey: string;
  appId: string;
}

const DRIVE_FILE_SCOPE = 'https://www.googleapis.com/auth/drive.file';
const GAPI_SRC = 'https://apis.google.com/js/api.js';
const GIS_SRC = 'https://accounts.google.com/gsi/client';

/** Pure helper: extract a usable Picker config from status, or null when disabled. */
export function pickerConfigFromStatus(status: DriveStatus | null): DrivePickerConfig | null {
  if (!status || !status.pickerEnabled) {
    return null;
  }
  const clientId = (status.pickerClientId ?? '').trim();
  const apiKey = (status.pickerApiKey ?? '').trim();
  const appId = (status.pickerAppId ?? '').trim();
  if (clientId === '' || apiKey === '' || appId === '') {
    return null;
  }
  return { clientId, apiKey, appId };
}

/** Pure helper: human-readable hint for why the picker is unavailable. */
export function pickerDisabledHint(status: DriveStatus | null): string {
  if (!status) {
    return 'Drive status is still loading.';
  }
  if (!status.configured) {
    return 'Server is not configured for Google OAuth — see setup docs.';
  }
  if (!status.connected) {
    return 'Connect Google Drive first, then choose from Drive.';
  }
  if (!status.pickerEnabled) {
    return 'Picker needs a Browser API key and Cloud project number: set DrivePublishing__GooglePickerApiKey and DrivePublishing__GooglePickerAppId on the server (see docs/drive-publishing.md). You can still paste ids for app-created files.';
  }
  return '';
}

/** Pure helper: normalize a Picker docs[0] payload into a selection. */
export function toPickerSelection(doc: {
  id?: unknown;
  name?: unknown;
  mimeType?: unknown;
}): DrivePickerSelection | null {
  if (typeof doc.id !== 'string' || doc.id.trim() === '') {
    return null;
  }
  const name = typeof doc.name === 'string' && doc.name.trim() !== '' ? doc.name : doc.id;
  const mimeType = typeof doc.mimeType === 'string' ? doc.mimeType : null;
  return { id: doc.id, name, mimeType };
}

function loadScriptOnce(src: string): Promise<void> {
  const existing = document.querySelector(`script[src="${src}"]`);
  if (existing) {
    return Promise.resolve();
  }
  return new Promise((resolve, reject) => {
    const script = document.createElement('script');
    script.src = src;
    script.async = true;
    script.defer = true;
    script.onload = () => resolve();
    script.onerror = () => reject(new Error(`Failed to load ${src}. Check internet access and retry.`));
    document.head.appendChild(script);
  });
}

type GapiWindow = Window & {
  gapi?: { load: (api: string, callback: () => void) => void };
  google?: {
    accounts?: {
      oauth2?: {
        initTokenClient: (config: {
          client_id: string;
          scope: string;
          callback: (response: { access_token?: string; error?: string }) => void;
        }) => { requestAccessToken: (options?: { prompt?: string }) => void };
      };
    };
    picker?: {
      ViewId?: { FOLDERS?: string; DOCS?: string };
      DocsView?: new (viewId?: string) => {
        setIncludeFolders: (include: boolean) => unknown;
        setSelectFolderEnabled: (enabled: boolean) => unknown;
        setMimeTypes: (types: string) => unknown;
      };
      PickerBuilder?: new () => PickerBuilderLike;
      Action?: { PICKED?: string; CANCEL?: string };
    };
  };
}

export interface PickerBuilderLike {
  addView: (view: unknown) => unknown;
  setOAuthToken: (token: string) => unknown;
  setDeveloperKey: (key: string) => unknown;
  setAppId: (appId: string) => unknown;
  setCallback: (callback: (data: { action?: string; docs?: unknown[] }) => void) => unknown;
  build: () => { setVisible: (visible: boolean) => void };
}

export type PickerCallback = (data: { action?: string; docs?: unknown[] }) => void;

/**
 * Pure builder wiring: applies the Drive Picker configuration in the
 * documented order (view, OAuth token, API key, Cloud project number via
 * setAppId, callback) and builds the picker. setAppId is required for the
 * drive.file scope so picked files become accessible to this Cloud project.
 * Extracted for unit testing without loading Google scripts.
 */
export function configurePickerBuilder(
  builder: PickerBuilderLike,
  view: unknown,
  config: DrivePickerConfig,
  token: string,
  callback: PickerCallback,
): { setVisible: (visible: boolean) => void } {
  builder.addView(view);
  builder.setOAuthToken(token);
  builder.setDeveloperKey(config.apiKey);
  // Required for drive.file: the Cloud project number lets the picked
  // files be granted to this app. Omitting it leaves existing-file adoption
  // unreliable (Picker selections return ids the backend cannot read).
  builder.setAppId(config.appId);
  builder.setCallback(callback);
  return builder.build();
}

async function ensurePickerApi(): Promise<void> {
  await loadScriptOnce(GAPI_SRC);
  const w = window as unknown as GapiWindow;
  if (!w.gapi) {
    throw new Error('Google Picker API failed to load. Check internet access and retry.');
  }
  await new Promise<void>((resolve) => {
    w.gapi!.load('picker', () => resolve());
  });
  // Poll briefly until the picker namespace appears.
  for (let attempt = 0; attempt < 50; attempt += 1) {
    if ((window as unknown as GapiWindow).google?.picker?.PickerBuilder) {
      return;
    }
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  throw new Error('Google Picker did not initialize. Reload the page and retry.');
}

async function ensureGis(): Promise<void> {
  await loadScriptOnce(GIS_SRC);
  const w = window as unknown as GapiWindow;
  if (!w.google?.accounts?.oauth2) {
    throw new Error('Google Identity Services failed to load. Check internet access and retry.');
  }
}

function requestPickerToken(clientId: string): Promise<string> {
  const w = window as unknown as GapiWindow;
  const oauth2 = w.google?.accounts?.oauth2;
  if (!oauth2) {
    return Promise.reject(new Error('Google Identity Services is not loaded.'));
  }
  return new Promise((resolve, reject) => {
    try {
      const client = oauth2.initTokenClient({
        client_id: clientId,
        scope: DRIVE_FILE_SCOPE,
        callback: (response) => {
          if (response.access_token) {
            resolve(response.access_token);
          } else {
            reject(new Error(response.error ?? 'Google authorization was denied or cancelled.'));
          }
        },
      });
      // Reuse the backend OAuth consent when possible; fall back to an
      // account chooser only if Google requires it.
      client.requestAccessToken({ prompt: '' });
    } catch (failure) {
      reject(failure instanceof Error ? failure : new Error('Could not start Google Picker authorization.'));
    }
  });
}

/**
 * Show the Google Picker dialog for one folder or Markdown file.
 *
 * The Picker token is obtained in the browser via Google Identity Services
 * using the public OAuth client id — the server's refresh/access tokens are
 * never exposed to the frontend. Because the Picker uses the same Cloud
 * project, files the user picks become accessible to the app's drive.file
 * scope, so the backend can adopt and update them in place.
 */
export async function showDrivePicker(
  config: DrivePickerConfig,
  kind: 'folder' | 'markdown',
): Promise<DrivePickerSelection | null> {
  await ensurePickerApi();
  await ensureGis();
  const token = await requestPickerToken(config.clientId);
  const w = window as unknown as GapiWindow;
  const picker = w.google?.picker;
  if (!picker?.PickerBuilder || !picker.DocsView || !picker.ViewId || !picker.Action) {
    throw new Error('Google Picker did not initialize. Reload the page and retry.');
  }

  const view =
    kind === 'folder'
      ? new picker.DocsView(picker.ViewId.FOLDERS)
      : new picker.DocsView(picker.ViewId.DOCS);
  if (kind === 'folder') {
    (view.setIncludeFolders(true) as { setSelectFolderEnabled: (enabled: boolean) => unknown }).setSelectFolderEnabled(
      true,
    );
    view.setMimeTypes('application/vnd.google-apps.folder');
  } else {
    view.setIncludeFolders(true);
    view.setMimeTypes('text/markdown,text/plain');
  }

  return new Promise((resolve, reject) => {
    try {
      const builder = new picker.PickerBuilder!() as PickerBuilderLike;
      const built = configurePickerBuilder(
        builder,
        view,
        config,
        token,
        (data) => {
          const pickedAction = picker.Action?.PICKED;
          const cancelAction = picker.Action?.CANCEL;
          if (data.action === pickedAction && Array.isArray(data.docs) && data.docs.length > 0) {
            const first = data.docs[0] as { id?: unknown; name?: unknown; mimeType?: unknown };
            resolve(toPickerSelection(first));
          } else if (data.action === cancelAction) {
            resolve(null);
          }
        },
      );
      built.setVisible(true);
    } catch (failure) {
      reject(failure instanceof Error ? failure : new Error('Could not open Google Picker.'));
    }
  });
}
