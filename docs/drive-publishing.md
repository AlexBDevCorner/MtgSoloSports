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
  5. Enable the **Google Picker API** in the same Cloud project, create a
     **Browser API key** (HTTP referrers restricted to `http://localhost:8080/*`),
     and pass it as `DrivePublishing__GooglePickerApiKey` (public browser key,
     not a secret). Find the Cloud **project number** on the Cloud console
     dashboard and pass it as `DrivePublishing__GooglePickerAppId` (public
     identifier for `PickerBuilder.setAppId`, required for the `drive.file`
     scope so picked files become accessible to this project). Without both
     values the in-app **Choose from Drive** grant buttons stay disabled;
     pasted ids still work for app-created files. Restart after setting them.
 6. Open Reports, press **Connect Google Drive**, and approve the
   least-privilege `drive.file` consent. `drive.file` lets the app manage only
   files it created or that were explicitly granted — it cannot see or edit
   your whole Drive.

If credentials are missing, Reports shows “Server is not configured” and the
API returns `503` with setup guidance instead of failing obscurely.

## Destination configuration

- Prefer **Choose folder… / Choose Seasons… / Choose dossier… / Choose almanac…** (Google Picker,
  same Google account) to select the `mtshistory` folder, the existing `Seasons/`
  subfolder, and the two report files. Picker is the supported `drive.file` grant:
  files you pick become accessible to this Cloud project, so the backend can adopt
  and update them in place. The Picker token is minted in the browser via Google Identity Services
  with the public client id — server refresh/access tokens are never exposed.
  After picking, press **Save destination**, then **Verify access**.
  With `drive.file`, picking `mtshistory` alone does not grant its pre-existing
  `Seasons/` child: explicitly pick the `Seasons/` folder (or let publishing infer
  it from an already-granted adopted dossier) so no duplicate `Seasons/` folder is
  created. When an adopted dossier exists, publishing never auto-creates `Seasons/`;
  it reuses the inferred or explicitly configured folder and otherwise stops with
  guidance.
- You may also paste the `mtshistory` **folder id** (from the Drive URL:
  `…/folders/<id>`), the optional `Seasons/` **folder id**, and the two report
  **file ids** (from `…/file/d/<id>/…`).
  Manually named files such as `universe-almanac-newtest.md` keep their remote
  ids and custom names — they are never guessed, renamed, moved or deleted.
  An adopted id saved without an explicit file name keeps its current remote name;
  publishing never renames it to the generated default.
  Adopted ids are validated on publish: a dossier id must point to a Markdown
  file directly under the `Seasons/` subfolder, and an almanac id must point to
  a Markdown file directly under `mtshistory`. Ids outside the destination,
  folder ids, trashed files, or ids that are not visible (deleted or never
  granted — `drive.file` hides ungranted files as missing) stop with a
  recoverable conflict instead of overwriting unrelated Drive content or
  creating a `(1)` duplicate. Renames to an explicitly configured file name
  only happen for files already inside the destination.
- Pasting an id alone does **not** grant `drive.file` access to a pre-existing
  file or folder. The app can only read/write files it created or that you
  explicitly granted with **Choose from Drive**. After pasting ids, press
  **Verify access**: it reports per file/folder whether the grant is present
  (`ok`), missing (`needs-grant`), not visible (`not-found`, fail-closed with
  no duplicate), outside the destination, a folder, or needs explicit adoption
  — without creating anything. Re-pick flagged items with **Choose from Drive**,
  then Verify again before publishing. Drive **Open with** requires a public
  HTTPS app URL and is not available for localhost, so it is not the supported
  path here.
- Leave a file id empty to create a new file on the next publish, using the
  stable names `season-<N>-dossier.md` (under `Seasons/`) and
  `universe-almanac-<save-slug>.md`. With `drive.file`, an empty scoped
  listing does **not** prove a name is free (an existing but ungranted
  `Seasons/` folder or report is hidden as missing). Publishing therefore
  fails closed without creating a duplicate unless **Allow creating missing
  folders/files** is ticked for that Publish; Verify reports the same caveat.
  Tick the box only when you know no hidden pre-existing folder/file with that
  name exists, or adopt the existing object explicitly with Choose from Drive.
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
  only after the parent-folder and MIME checks pass. An adopted id that is no
  longer visible fails closed with guidance (clear the id to create anew, or
  re-pick a granted file) instead of creating a duplicate. Timeouts are
  retry-safe: publishing re-checks identity and content before creating, so
  retries never duplicate. Folder access is verified with a folder list
  operation, not a media download (folders return `fileNotDownloadable`).
- If a Drive file was edited independently since the last publish, publishing
  stops with a conflict instead of silently discarding edits. Tick **Overwrite
  independently edited Drive files** to replace them deliberately.
- Missing `Seasons/` or report files are created only when **Allow creating
  missing folders/files** is ticked; otherwise publishing stops with guidance
  and creates nothing, because an empty `drive.file` listing does not prove the
  name is free.
- Per-file outcomes (`created`/`updated`/`unchanged`/`failed`) plus the last
  published season/stage/phase are shown in the UI and kept server-side.
- Google or network failures return `502`/`401`/`409` diagnostics; simulation
  data is never rolled back, advanced or corrupted.

Tokens and destinations live in `/data/drive-publishing` (owner-only files),
separate from sports saves and backup ZIPs, and survive container restarts.
The Compose port is localhost-bound (`127.0.0.1:8080:8080`): the publishing
endpoints trust the local browser and hold Google refresh credentials
server-side, so they must not be exposed to the LAN. Mutating Drive endpoints
(`PUT destination`, `POST publish/verify/disconnect`) require an exact
same-origin (`scheme` + loopback host literal + port) for any `Origin`/
`Referer` header and fail closed with `403` otherwise; a non-loopback request
authority (DNS rebinding) is rejected even without headers, while headerless
loopback requests (e.g. curl) are allowed — use the local Reports page origin.
Parallel publishes for the same save are serialized per save so concurrent
first publishes cannot create duplicate `Seasons/` folders or reports.
**Disconnect** revokes the grant (best effort) and deletes local tokens;
destinations are kept for the next connect. Expired/revoked consent is
detected on the next publish and asks you to reconnect.

## Automatic publishing after each completed stage (MSS-090)

Opt-in **Automatically publish after each completed stage** on Reports/Drive
(off by default) removes the need to click Publish after every stage:

- Enabling records the current durable checkpoint (season, global stage,
  phase) as the requested state; the background pump then uploads the current
  season dossier and universe almanac through the same idempotent publisher.
- The trigger fires only after a committed durable boundary: a completed
  global stage (single, bulk, fast simulation, Next Event), season completion
  and next-season start. Completed rounds, partial league stages and
  still-rolling transactions never trigger.
- Burst requests coalesce: bulk simulation of dozens of stages publishes the
  latest stable snapshot, not each intermediate state, and per-save
  serialization prevents a stale report from overwriting a newer one.
- Requested vs last-published checkpoints live in app-level settings outside
  the save SQLite, so restart/reconnect reconciles unpublished work. Statuses
  are `Up to date`, `Publishing`, `Pending`, `Failed`, `Disconnected`
  (`Disabled` when off); a green status never shows for a failed upload.
- Transient failures retry with capped backoff; auth/permission failures pause
  automatic work and prompt reconnect. Manual **Publish now** stays as the
  override/retry; disabling cancels pending work without deleting Drive files.
- New seasons create a new stable `Seasons/season-N-dossier.md` while prior
  seasons are preserved; the almanac keeps its file.

## Full historical library sync (MSS-091)

**Sync all changed reports** on Reports/Drive extends the same in-place rules to the whole
organized library:

```text
MtgSoloSports/mtshistory/Seasons/season-<N>-dossier.md     (all seasons)
MtgSoloSports/mtshistory/Color Cup/Cups/cup-color-season-<N>-edition.md
MtgSoloSports/mtshistory/Color Cup/Teams Histories/cup-color-team-<key>.md
MtgSoloSports/mtshistory/Type Cup/Cups/cup-type-season-<N>-edition.md
MtgSoloSports/mtshistory/Type Cup/Teams Histories/cup-type-team-<key>.md
MtgSoloSports/mtshistory/universe-almanac-<save-slug>.md
```

- **Preview first.** *Preview library* inventories every supported report and reports
  `created`/`updated`/`unchanged`/`failed` counts per category without any Drive write or
  destination save, so you can see what a sync would do.
- **Folder adoption, not duplication.** The destination now maps the six Cup folders
  (`Color Cup`, `Color Cup/Cups`, `Color Cup/Teams Histories`, `Type Cup`, `Type Cup/Cups`,
  `Type Cup/Teams Histories`) alongside `Seasons/`. Explicitly adopted folders keep custom
  names; unadopted folders are auto-resolved by default name (`Color Cup`, `Cups`,
  `Teams Histories`, ...) and only created when **Allow creating missing folders/files** is
  ticked. An invisible-but-existing folder is never duplicated: resolution fails closed with
  adoption guidance instead.
- **File adoption.** Existing hand-uploaded reports are adopted by stable semantic identity
  (save id, report type, season number or team key): same content adopts the existing file
  with no write; changed content updates that exact file id. Enter the semantic key
  (`1` for dossiers, `color:1`/`type:2` for editions, `color:red`/`type:Dragon` for teams)
  and use **Choose season/edition/team file…** (Google Picker Markdown grant) so the
  hand-uploaded file becomes visible under the `drive.file` scope, then Save destination.
  One same-named file with
  different content stops with the existing id so you can adopt it explicitly; two or more
  same-named files (e.g. a ` (1)` duplicate) stop with duplicate guidance and create
  nothing. No deletion, rename or overwrite of unknown files, ever.
- **Efficient change detection.** Each Markdown payload is hashed ignoring the volatile
  `Generated:` line, so repeat syncs with unchanged simulation history write nothing.
  Progress persists incrementally per category, so an interrupted sync resumes without
  re-uploading settled files; failed files can be retried with the same Sync action.
  Bounded `MaxFiles` pages report `truncated: true` with `nextSkip`: resume with
  `skip = nextSkip` until `truncated` is false (the UI loops automatically, up to 20
  pages). A truncated page never advances the library checkpoint until the final page
  settles.
- **Automatic mode** refreshes the almanac and current dossier as before, **plus** the Cup
  edition/team histories that actually changed after the relevant Cup selection or
  finalization (every changed source season since the last settled publish, so a bulk
  `SimulateSeasons` run refreshes all its new Cup reports, not just the latest season).
  New Cup edition/team files are created
  automatically once the four Cup leaf folders are adopted; otherwise auto mode stays
  fail-closed and the next manual **Sync all changed reports** (with creation confirmed)
  is the prerequisite. Ordinary league stages never re-upload the
  whole team-history archive: unchanged fingerprints report `unchanged` with no writes.
- **Freshness per category.** Status shows the last library sync time, per-category
  created/updated/unchanged/failed counts and the last library error, alongside the
  single-publish checkpoint. Verify access now also checks the six Cup folders
  (`ok`/`needs-grant`/`not-found`/`needs-adoption`/`duplicate`/`outside-destination`).

## Limitations

- Publishing needs the app running and an internet connection; automatic sync
  works only while the app runs and is authorized (no background processing
  when shut down).
- Only the curated library above is published (season dossiers, Color/Type Cup
  editions and team histories, almanac). Athlete, standings and league profile
  reports stay download-only until explicitly configured later.
- Drive updates do **not** replace separately uploaded ChatGPT Project copies:
  re-attach the files in ChatGPT after publishing.
- To link/refresh a Drive folder inside ChatGPT, use ChatGPT's own Drive
  picker on the same folder; this app's OAuth grant is separate and cannot be
  transferred.

## Manual end-to-end checklist (opt-in, no live credentials in tests)

1. `docker compose up --build` with OAuth + Picker env vars set; open Reports.
2. Connect the account; use **Choose from Drive** to grant the mtshistory folder and the existing season/almanac Markdown files, then Save destination.
3. Verify access: all adopted ids report `ok` (re-pick with Choose from Drive if `needs-grant`; `not-found` means deleted or never granted and fails closed with no duplicate).
4. Publish: both files update in place (same ids), outcomes `created`/`updated`.
5. Advance a stage; publish again: same ids, changed content, outcomes `updated`.
6. Start the next season; publish: a new `Seasons/season-N-dossier.md` is created/updated while the prior season's file keeps its id and content; almanac keeps its id.
7. Publish with no sporting change: both `unchanged`.
8. Adopt an id outside the destination; publish/verify: recoverable `outside-destination`/`is-folder` with no overwrite.
9. Disconnect (or revoke in the Google account); publish: recoverable `401`.
10. Remove server credentials; connect: recoverable `503`.
11. Simulate a transient failure; publish: recoverable `failed` outcome, retry clean.
12. Adopt the Cup folders (`Color Cup/Cups`, `Color Cup/Teams Histories`, `Type Cup/Cups`,
    `Type Cup/Teams Histories`) with **Choose from Drive** or folder ids, then Save + Verify:
    all six Cup checks report `ok`. Adopt hand-uploaded season/Cup/team files by key with
    **Choose season/edition/team file…**, then Save + Verify (pasted ids alone do not grant
    `drive.file` access).
13. Preview library: per-category created/updated/unchanged/failed counts with no Drive writes.
14. Sync all changed reports: every season dossier, Cup edition/team history and the almanac
    land in place (same ids); repeat sync writes nothing. A manually uploaded ` (1)` duplicate
    surfaces as `duplicate` with no new file until resolved in Drive.
