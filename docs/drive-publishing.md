# Google Drive publishing (MSS-089)

One-click **Publish to Drive** on the Reports page updates the current season
dossier and universe almanac **in place** in your own Drive hierarchy:

```text
MtgSoloSports/mtshistory/universe-almanac-<save-slug>.md   (or an adopted custom name)
MtgSoloSports/mtshistory/Seasons/season-<N>-dossier.md
```

Existing download `.md`/`.json` actions are unchanged; publishing uploads only
the Markdown payloads, regenerated server-side from persisted reports with the
same content as the download endpoints.

## One-time setup (personal OAuth app)

MtgSoloSports runs locally and is not an internet-hosted service, so publishing
needs explicit user authorization from inside the app. The ChatGPT Google Drive
connector cannot be reused as application credentials.

1. In the [Google Cloud console](https://console.cloud.google.com/), create a
   project and enable the **Google Drive API**.
2. Configure the OAuth consent screen (External, add yourself as a test user is
   enough for personal use).
3. Create an **OAuth client ID** of type **Web application** with authorized
   redirect URI exactly:
   `http://localhost:8080/api/reports/drive/callback`
   (`localhost` over plain HTTP is permitted by Google for local development;
   the default Compose port is 8080).
4. Pass the credentials to the app (never commit them):
   `DrivePublishing__GoogleClientId`, `DrivePublishing__GoogleClientSecret`,
   and optionally `DrivePublishing__GoogleRedirectUri` if you run on a
   different host port. Restart the container after setting them.
5. Open Reports, press **Connect Google Drive**, and approve the
   least-privilege `drive.file` consent. `drive.file` lets the app manage only
   files it created or that were explicitly granted — it cannot see or edit
   your whole Drive.

If credentials are missing, Reports shows “Server is not configured” and the
API returns `503` with setup guidance instead of failing obscurely.

## Destination configuration

- Paste the `mtshistory` **folder id** (from the Drive URL: `…/folders/<id>`).
- Explicitly **adopt** the two report files by pasting their **file ids**
  (from `…/file/d/<id>/…`). Manually named files such as
  `universe-almanac-newtest.md` keep their remote ids and custom names — they
  are never guessed, renamed, moved or deleted. Adopted ids are validated on
  publish: a dossier id must point to a Markdown file directly under the
  `Seasons/` subfolder, and an almanac id must point to a Markdown file
  directly under `mtshistory`. Ids outside the destination, folder ids, or
  trashed files stop with a recoverable conflict instead of overwriting
  unrelated Drive content. Renames to the configured `season-N-dossier.md`
  name only happen for files already inside the destination.
- Pasting an id alone does **not** grant `drive.file` access to a
  pre-existing file or folder. The app can only read/write files it created
  or that you explicitly granted via Google Picker / Drive **Open with**
  (right-click the file/folder in Drive, open it with this app using the same
  Google account, or pick it in Picker). After pasting ids, press **Verify
  access**: it reports per file/folder whether the grant is present (`ok`),
  missing (`needs-grant`), outside the destination, a folder, or needs
  explicit adoption — without creating anything. Grant the flagged items in
  Drive, then Verify again before publishing.
- Leave a file id empty to create a new file on the next publish, using the
  stable names `season-<N>-dossier.md` (under an auto-created `Seasons/`
  subfolder) and `universe-almanac-<save-slug>.md`.
- Each season keeps its own dossier mapping. Publishing season N+1 creates or
  updates `Seasons/season-(N+1)-dossier.md` while preserving the prior
  season's Drive file and mapping; it never overwrites the previous season's
  dossier under the old name.
- If a name already exists at create time, publishing stops with the existing
  id and asks you to adopt it — no `(1)` duplicates.
- Each save has its own destination. Reusing another save's folder/file ids
  needs the **Confirm cross-save use** checkbox, so one universe is never
  published into another's destination by accident.

## Publish now

- Regenerates the **current/latest** season dossier and the almanac from the
  selected save only, then uploads both Markdown files.
- Unchanged reports compare the meaningful Markdown content (the volatile
  `Generated:` timestamp is ignored) and report `unchanged` — no Drive writes.
- Previously adopted/created reports update **in place** (same file id/link)
  only after the parent-folder and MIME checks pass. Timeouts are retry-safe:
  publishing re-checks identity and content before creating, so retries never
  duplicate.
- If a Drive file was edited independently since the last publish, publishing
  stops with a conflict instead of silently discarding edits. Tick **Overwrite
  independently edited Drive files** to replace them deliberately.
- Per-file outcomes (`created`/`updated`/`unchanged`/`failed`) plus the last
  published season/stage/phase are shown in the UI and kept server-side.
- Google or network failures return `502`/`401`/`409` diagnostics; simulation
  data is never rolled back, advanced or corrupted.

Tokens and destinations live in `/data/drive-publishing` (owner-only files),
separate from sports saves and backup ZIPs, and survive container restarts.
**Disconnect** revokes the grant (best effort) and deletes local tokens;
destinations are kept for the next connect. Expired/revoked consent is
detected on the next publish and asks you to reconnect.

## Limitations

- Publishing needs the app running and an internet connection; it is strictly
  user-triggered (no automatic publish — see MSS-090).
- Only the current dossier + almanac are published (no bulk history upload —
  see MSS-091). `Color Cup`/`Type Cup` team-history destinations are reserved
  for future sync and not published by this task.
- Drive updates do **not** replace separately uploaded ChatGPT Project copies:
  re-attach the files in ChatGPT after publishing.
- To link/refresh a Drive folder inside ChatGPT, use ChatGPT's own Drive
  picker on the same folder; this app's OAuth grant is separate and cannot be
  transferred.

## Manual end-to-end checklist (opt-in, no live credentials in tests)

1. `docker compose up --build` with OAuth env vars set; open Reports.
2. Connect the account; adopt the existing season/almanac Markdown files.
3. Verify access: all adopted ids report `ok` (grant via Picker / Open with if `needs-grant`).
4. Publish: both files update in place (same ids), outcomes `created`/`updated`.
5. Advance a stage; publish again: same ids, changed content, outcomes `updated`.
6. Start the next season; publish: a new `Seasons/season-N-dossier.md` is created/updated while the prior season's file keeps its id and content; almanac keeps its id.
7. Publish with no sporting change: both `unchanged`.
8. Adopt an id outside the destination; publish/verify: recoverable `outside-destination`/`is-folder` with no overwrite.
9. Disconnect (or revoke in the Google account); publish: recoverable `401`.
10. Remove server credentials; connect: recoverable `503`.
11. Simulate a transient failure; publish: recoverable `failed` outcome, retry clean.
