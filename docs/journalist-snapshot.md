# Journalist snapshot helper (MSS-133)

A small local CLI that fetches one verified, read-only SQLite snapshot of an
explicitly configured save from the default container deployment. It exists so
a locally running journalist agent can inspect game data on demand without
manual downloads, Docker administration access, or Google Drive.

Non-goals: stage evidence/standings interpretation, article generation and
archive management belong to MSS-134/MSS-135. Google Drive publishing stays
optional and unchanged.

## What it does

- `list-saves` enumerates saves via the existing `GET /api/saves` for one-time
  setup. It never selects a save; acquisition always needs the exact save id.
- `acquire` downloads the save through the existing
  `GET /api/saves/{saveId}/export` route (the same artifact `SaveStore`
  builds with `VACUUM INTO`), or reuses an already downloaded bundle file
  fully offline with `--bundle`. It validates archive shape, checksum,
  manifest/database identity and supported schema/rules versions, then
  publishes one replaceable working snapshot. A failed download or validation
  never replaces the previous valid snapshot.
- `info` reprints the published metadata; `query` runs bounded read-only
  `SELECT` queries against the same snapshot, so all follow-up questions for
  one article use identical data without fetching again.

The only game access is the default container's loopback export endpoint.
No container shell, Docker socket, volume remap, new listening port, or
application credential is used. The helper talks to the loopback app with
plain `GET` requests for convenience; that is **not** a read-only security
boundary — the local API remains full-access, and anyone who can reach the
loopback port can mutate saves. Keep the Compose port localhost-bound
(`127.0.0.1:8080`, the default) and treat the helper as a careful client,
not a sandbox.

## Prerequisites

- .NET 10 SDK (the game's existing development prerequisite).
- The game running locally: `docker compose up --build` (serves
  `http://localhost:8080`, per-save databases in the `mtgsolosports-data`
  volume under `/data/saves`).
- Windows PowerShell for the one-liners below.

## Setup, download, inspect (PowerShell)

```powershell
dotnet build tools/SaveSnapshot/MtgSoloSports.SaveSnapshot.csproj --configuration Release
```

```powershell
Copy-Item tools/SaveSnapshot/snapshot.config.example.json tools/SaveSnapshot/snapshot.config.json
```

```powershell
dotnet run --project tools/SaveSnapshot --configuration Release --no-build -- list-saves --base-url http://localhost:8080
```

Paste the exact `saveId` for your game into
`tools/SaveSnapshot/snapshot.config.json` (keep the real id and any machine
paths in that untracked file only; the tracked example holds placeholders).

```powershell
dotnet run --project tools/SaveSnapshot --configuration Release --no-build -- acquire --config tools/SaveSnapshot/snapshot.config.json
```

```powershell
dotnet run --project tools/SaveSnapshot --configuration Release --no-build -- info --config tools/SaveSnapshot/snapshot.config.json
```

```powershell
dotnet run --project tools/SaveSnapshot --configuration Release --no-build -- query --config tools/SaveSnapshot/snapshot.config.json --sql "SELECT SeasonNumber, HasSuperleague, IsComplete FROM Seasons ORDER BY SeasonNumber;"
```

Offline (no game contact after the bundle is saved):

```powershell
dotnet run --project tools/SaveSnapshot --configuration Release --no-build -- acquire --config tools/SaveSnapshot/snapshot.config.json --bundle ./my-save.mtgsave.zip
```

`acquire` prints the snapshot metadata JSON to stdout (logs go to stderr).
Exit codes: `0` success, `1` operational failure (with an actionable stderr
message), `2` usage error.

## Working directory layout

Relative `workingDirectory` values resolve against the config file's
directory. After a successful `acquire` the directory holds exactly:

- `save.db` — the validated snapshot database (single replaceable copy; no
  per-stage database retention).
- `manifest.json` — the export manifest (save identity, export timestamp,
  schema/rules/RNG versions, checksum).
- `snapshot.json` — compact metadata: snapshot location, save identity,
  source/export and acquisition timestamps, schema/rules versions, content
  fingerprint (`databaseSha256`), current season/phase, and per-season
  stage/round coverage.

Publishing is atomic: validation happens in a staging directory and the three
files are replaced only after everything succeeds, metadata last. A torn or
interrupted refresh fails the next `info`/`query` with a re-run instruction
instead of serving stale data as current.

Generated bundles (`*.mtgsave.zip`), databases (`*.db`, `*-wal`, `*-shm`),
`snapshot-data/` directories and `tools/SaveSnapshot/snapshot.config.json`
are ignored by Git; only `snapshot.config.example.json` is tracked.

## Read-only inspection

- Every inspection opens `save.db` with `Mode=ReadOnly` plus
  `PRAGMA query_only=ON`. No bootstrap, import, `OpenSave`, migration,
  seeding, activation/backfill or simulation path is ever invoked.
- `query` accepts a single `SELECT` (or `WITH ... SELECT`) statement.
  Mutating keywords, stacked statements and `PRAGMA` are rejected before
  SQLite sees them, and the read-only connection rejects writes anyway.
- Results are capped (`--limit`, default 200, max 1000) and flagged with
  `truncated`. Keep follow-up questions to small aggregates and keyed
  lookups, for example:
  - `SELECT SeasonNumber, HasSuperleague, IsComplete FROM Seasons ORDER BY SeasonNumber;`
  - `SELECT COUNT(*) FROM SaveAthletes;`
  - `SELECT Name FROM SaveAthletes ORDER BY Name;` with `--limit 5`
- SQLite may keep empty WAL bookkeeping beside the file after reads; the
  validated database bytes themselves are never modified (hash-verified in
  tests), and the next `acquire` clears stale bookkeeping before publishing.

## Failures

| Symptom | Meaning | Action |
|---|---|---|
| `Game app at '...' is unavailable` | Container not running / wrong URL | `docker compose up --build`, retry |
| `Save '...' was not found` | Unknown save id (HTTP 404) | `list-saves`, paste the exact id |
| `not a valid save bundle` / `exactly 2 entries` / `unexpected entry` | Bad or unsafe archive | Re-download; never hand-edit bundles |
| `checksum does not match` | Corrupt/tampered payload | Re-download; previous snapshot kept |
| `does not match the configured save id` | Bundle is for another save | Check `--save-id` / config; no fallback |
| `newer than supported` / `rules version ... not supported` | Incompatible build | Upgrade the helper; no migration/repair |

## Verification

Focused tests drive the real CLI in-process against a loopback stub serving
a genuine `SaveStore.ExportAsync` artifact, plus offline-bundle acquisition:

```powershell
dotnet test tests/MtgSoloSports.Tests/MtgSoloSports.Tests.csproj --configuration Release --no-build --no-restore --filter "FullyQualifiedName~SnapshotHelper"
```

Manual smoke against the default Compose app: start the container, create or
open a save in the UI, run the setup/download/inspect one-liners above, then
confirm `snapshot.json` (`saveId`, `exportedUtc`, `databaseSha256`, `seasons`)
matches the save you played. Compare the source and snapshot hashes and the
`RngStates` row before and after inspection to confirm nothing moved. No
Docker daemon or live user data is needed in CI.
