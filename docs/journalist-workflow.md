# Journalist workflow — "Write the latest Superleague stage review" (MSS-135)

A repeatable local agent workflow that fetches fresh evidence, writes a stage
review in chat, shows it in chat, then saves it with its sources and preserves
editorial continuity across chats. No scheduler, no model-service integration,
no global config edits, no Drive changes, no publication.

The game stays the source of truth. Saved articles and short editorial notes
preserve prior interpretations, predictions and storylines; they never replace
querying the evidence. The agent does all editorial writing in chat through the
existing snapshot/evidence helper (MSS-133/MSS-134); there is no in-app
"generate article" feature, no model API call and no rigid prose generator.

Start here for "Write the latest Superleague stage review". For snapshot
download and stage-evidence details see
[journalist-snapshot.md](journalist-snapshot.md); for one-click Drive dossiers
see [reports.md](reports.md).

## Non-goals

- Scheduled/automatic generation, notifications, background monitoring.
- Embedding a model API in the game, subscription/usage optimization, new UI.
- Google Drive changes, automatic uploads, GitHub publication of articles.
- Simulation/rule changes, importing/restoring saves, forensic reconstruction.
- A new settings framework, a prescribed subscription/model, a prose template
  engine. Style, length and emphasis are simple local preferences you state in
  chat (for example "300 words, focus on the title race").

## Prerequisites

- .NET 10 SDK (the game's existing development prerequisite).
- The game running locally: `docker compose up --build` (serves
  `http://localhost:8080`, per-save databases in the `mtgsolosports-data`
  volume under `/data/saves`).
- Windows PowerShell for the one-liners below (Linux `bash` works with the
  same `dotnet` arguments; paths use the OS separator).
- A local agent chat with file access to this repository checkout, the helper
  (`tools/SaveSnapshot`) and your archive directory (see below). No skill
  install, no new service, no Drive connection, no container change is needed.
  The normal path needs no Docker administration.

## One-time setup (per machine, per save)

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
Confirm `archiveDirectory` points where you want durable reviews (default
`./journalist-archive`, resolved against the config file, ignored by Git):

```powershell
dotnet run --project tools/SaveSnapshot --configuration Release --no-build -- archive init --config tools/SaveSnapshot/snapshot.config.json
```

`archive init` creates `save-<saveId>/save.json` and `save.json`/`index.json`
under the archive root. It is idempotent for the same save and never touches
another save's directory. Two saves with the same display name stay isolated
by save id, never by name.

Local access summary: the helper downloads via the loopback app
(`GET /api/saves`, `GET /api/saves/{id}/export`), inspects the snapshot
read-only (`Mode=ReadOnly` + `query_only`, bounded `SELECT` only) and writes
only under the configured archive root. Keep the Compose port localhost-bound
(`127.0.0.1:8080`, the default). The loopback app is full-access for anyone
who can reach the port; the helper is a careful client, not a sandbox.
Existing approval boundaries stay as-is; this guide promises no
"no approvals on every host" and broadens no global permissions.

## Per-request sequence

Every review follows the same order. State the exact target in chat so the
next chat can reconstruct it from files alone.

1. Acquire a fresh snapshot (fails visibly, never serves stale as current):
   ```powershell
   dotnet run --project tools/SaveSnapshot --configuration Release --no-build -- acquire --config tools/SaveSnapshot/snapshot.config.json
   ```
   Offline (no game contact after a bundle is saved):
   ```powershell
   dotnet run --project tools/SaveSnapshot --configuration Release --no-build -- acquire --config tools/SaveSnapshot/snapshot.config.json --bundle ./my-save.mtgsave.zip
   ```
   On acquisition failure report the problem and stop; do not invent a
   stage. Offline use requires explicit `--bundle` selection and disclosure
   of snapshot freshness (`exportedUtc`/`acquiredUtc`/`databaseSha256`).
2. Resolve and state the exact target. Default is the latest completed
   Superleague stage (all its rounds and awarded championship points):
   ```powershell
   dotnet run --project tools/SaveSnapshot --configuration Release --no-build -- evidence --config tools/SaveSnapshot/snapshot.config.json > ./stage-evidence.json
   ```
   Historical (never silent fallback; an unfinished next stage never
   substitutes):
   ```powershell
   dotnet run --project tools/SaveSnapshot --configuration Release --no-build -- evidence --config tools/SaveSnapshot/snapshot.config.json --season 2 --stage 5 > ./stage-evidence.json
   ```
   Save the evidence stdout verbatim to a file; the commit step needs that
   file. State `season N stage M` in chat before writing.
3. Read relevant prior coverage (bounded, not the full archive):
   ```powershell
   dotnet run --project tools/SaveSnapshot --configuration Release --no-build -- archive context --config tools/SaveSnapshot/snapshot.config.json --season 2 --stage 5 > ./stage-context.json
   ```
   Omit `--season/--stage` only for the latest-stage review (recent reviews
   plus the current season notebook). For a historical request always pass
   the same cutoff; later articles and later notebook entries are excluded
   by construction and listed as omitted. Use targeted follow-ups for older
   seasons/athletes instead of raising `--recent`:
   ```powershell
   dotnet run --project tools/SaveSnapshot --configuration Release --no-build -- evidence --config tools/SaveSnapshot/snapshot.config.json --season 1 --stage 12 --athlete "Card Name"
   dotnet run --project tools/SaveSnapshot --configuration Release --no-build -- query --config tools/SaveSnapshot/snapshot.config.json --sql "SELECT SeasonNumber, StageNumber, StageRank FROM StageStandings WHERE SeasonId = 1 AND LeagueId = 2 ORDER BY StageNumber LIMIT 5;"
   ```
4. Investigate selected questions on the same snapshot (`query` only; no
   background calls to the advancing app). Keep follow-ups small and keyed.
5. Write and check the article in chat. Keep factual results separate from
   editorial interpretation and speculation. Prior predictions return as
   prior predictions, never as asserted facts. Check every score/gap/title
   and every scoped first/best/record claim against the MSS-134 evidence
   (`Stages:…`, `Rounds:…`, `StageStandings:…`, `SeasonStandings:…` refs).
   Do not invent interviews, quotations, injuries, motives or incidents.
6. Show the article in chat.
7. Save the article with its sources and update continuity (bounded):
   ```powershell
   dotnet run --project tools/SaveSnapshot --configuration Release --no-build -- archive commit --config tools/SaveSnapshot/snapshot.config.json --season 2 --stage 5 --article ./my-review.md --evidence ./stage-evidence.json --note "Leader extends to +12.000; rookie watch: <name>."
   ```
   `--note` (or `--note-file`) is 1–3 sentences of continuity: what changed,
   what to watch, what was predicted. The helper appends one dated
   `## Season N Stage M` entry to `season-NN/notebook.md` with cutoff,
   fingerprints and generation metadata; human notes anywhere are preserved
   because the helper only appends. The commit stores the evidence file
   verbatim beside the article plus a small versioned `.meta.json`
   (archive/evidence format versions, snapshot fingerprint, covered cutoff,
   stage/checksum provenance, revision, conflict flag) and updates the
   per-save `index.json`. The article's factual basis stays identifiable
   after the working database is refreshed.

## What gets stored where

Archive root (default `tools/SaveSnapshot/journalist-archive/`, untracked):

```text
journalist-archive/
  save-11111111-2222-3333-4444-555555555555/
    save.json                 # archiveFormatVersion, saveId, saveName
    index.json                # per-save seasons/stages, revisions, checksums
    season-02/
      stage-01.md             # human-owned article (edits preserved)
      stage-01.meta.json      # versioned provenance + revision + conflict
      stage-01.evidence.json  # evidence stdout verbatim (source of truth copy)
      stage-01.r1.md / .r1.meta.json / .r1.evidence.json  # prior rev backups
      notebook.md             # dated Season/Stage entries, helper-appends only
```

Identity is save-ID/season/stage. Writes stay under the configured archive
root; `commit` rejects target/evidence mismatches, another save's evidence,
and a torn entry. A repeat for the same stage without `--revision` fails
instead of overwriting; with `--revision` the prior article/meta/evidence are
copied to `stage-XX.r<N>.*` first. Human edits to `stage-XX.md` or
`notebook.md` are preserved: `commit` without `--revision` refuses to
overwrite them, `context` reads the current disk text (including edits), and
`commit --revision` backs the edited text up before replacing.

No full database copy is made per article (only the compact single-stage
evidence JSON). Never commit the archive to Git, never push it to GitHub and
never publish it elsewhere; it is durable user-owned local data. Working
snapshots (`snapshot-data/`, `save.db`, `snapshot.json`) stay replaceable and
untracked.

## Continuity rules the helper enforces

- Bounded by default: `context` returns the last `--recent` reviews
  (default 3, max 10) plus the season notebook excerpt (12,000 chars max).
  It never loads the full archive per request.
- Historical exclusion: with `--season/--stage`, articles after the cutoff
  are excluded (`excludedLaterArticles`), notebook entries after the cutoff
  are excluded (`excludedLaterNotebookEntries`) and unscoped notebook
  sections are excluded (`excludedUnscopedNotebookSections`). Only dated
  `## Season N Stage M` entries at or before the cutoff are shown. Make new
  notebook content dated/scoped; do not trust a current unscoped season
  summary for a historical review.
- First review / missed stages / new seasons: `context` discloses "no
  archived reviews yet", "no reviews for season N at or before stage M" and
  per-season gaps. Default to the requested/latest stage and disclose gaps;
  never auto-create reviews for uncovered stages. A new chat reconstructs
  continuity from `index.json` + recent articles + notebooks alone.
- Game advances after acquisition: the article keeps its original
  cutoff/snapshot provenance (`databaseSha256`, `exportedUtc`, `stageChecksum`,
  evidence generation metadata). `commit` records the working snapshot at
  commit time separately (`snapshotAtCommit`) so freshness vs evidence is
  explicit.
- Restored/replayed history: if the same save/season/stage is committed with
  a different `databaseSha256` or `stageChecksum`, `commit --revision`
  succeeds but flags `conflict: true`, records
  `priorDatabaseSha256`/`priorStageChecksum` and preserves earlier text in
  `stage-*.r<N>.*` backups. Do not merge incompatible timelines; choose a
  deliberate new revision or a different `--archive` root and keep earlier
  provenance.

## Fact-check checklist (before `commit`)

- [ ] Every score/gap/title matches the evidence JSON (thousandths +
  display strings; championship points are scoring-table awards, no bonus
  multiplier, no career prestige substitution).
- [ ] Rank/gap changes use `rankBefore`/`rankAfter`, `gapBefore`/`gapAfter`
  from `championshipAfter` (null for stage one is expected and disclosed).
- [ ] Scoped first/best/record claims cite the bounded history actually in
  evidence (same-season winners + prior champions); unavailable context is
  an omission with a reason, never an inference.
- [ ] No interviews, quotes, injuries, motives or incidents beyond source data.
- [ ] Cutoff stated (`season N stage M`), later events excluded, snapshot
  freshness disclosed (`exportedUtc`, `databaseSha256`).

## Example prompts

Initial (latest; one-time save configuration already done):

```text
Write the latest Superleague stage review for my save.
Config: tools/SaveSnapshot/snapshot.config.json. Archive: default.
Style: ~400 words, lead with the stage winner, then the title race, then one watchlist note.
After showing the article, save it with --note "one-line continuity" and report the archive paths.
```

Historical:

```text
Write the Superleague season 2 stage 5 review as a historical request.
Use --season 2 --stage 5 for both evidence and archive context; exclude later outcomes.
Check all scores against the evidence refs before showing the article.
```

Follow-up (revision):

```text
Revise the season 2 stage 5 review: tighten the middle, keep all numbers.
Commit with --revision and confirm the prior rev 1 text is backed up.
```

Athlete follow-up on the same snapshot:

```text
Same snapshot, no re-acquire: what did <Card Name> do in season 2 stage 5
(16 placements, last-5 form, prior wins/titles)? Cite evidence refs.
```

A new chat starts with the config + archive paths only; it must not rely on
another chat's transcript.

## Example layout (after two stages)

```text
journalist-archive/save-<saveId>/
  index.json
  season-02/notebook.md
  season-02/stage-01.md / stage-01.meta.json / stage-01.evidence.json
  season-02/stage-02.md / stage-02.meta.json / stage-02.evidence.json
```

## Troubleshooting

| Symptom | Meaning | Action |
|---|---|---|
| `Game app at '...' is unavailable` | Container down / wrong URL | `docker compose up --build`, retry `acquire` |
| `Save '...' was not found` | Unknown save id (404) | `list-saves`, paste exact id; helper never picks one |
| `No completed Superleague stage` | Season 1 only / nothing finished | Finish a Superleague stage in the game, re-`acquire` |
| `partial-stage` | Target has < `LeagueSize` standings or < `RoundsPerStage` rounds | Wait for the stage to complete; never present partial as final |
| `not-found` | Explicit `--season/--stage` beyond the snapshot | Check the target; no silent fallback |
| `corrupt` | Payload/checksum mismatch | Re-`acquire`; never invent results |
| `No journalist archive ... Run 'archive init'` | Archive not created | `archive init --config …` |
| `does not match the configured save` | Evidence/config/archive save ids differ | Check `--config`/`--evidence`/`--archive`; no cross-save writes |
| `does not match the requested season/stage` | Evidence target vs `--season/--stage` differ | Re-run `evidence` for the intended cutoff; save stdout verbatim |
| `already archived ... needs '--revision'` | Repeat without explicit revision | Add `--revision` (backs prior text to `.r<N>.*`) or fix the target |
| `already exists ... refusing to overwrite recoverable prior text` | Backup collision | Inspect `stage-*.r<N>.*`; resolve manually, no auto-overwrite |
| `with provenance conflict` | Same stage, different fingerprint (restore/replay) | Deliberate `--revision` or new `--archive`; earlier text preserved |
| `Article file must have '.md'` / `exceeds 32000+` | Article shape | Keep articles Markdown, compact (32 KB max) |
| Offline staleness | `--bundle` used / game advanced | Disclose `exportedUtc`/`databaseSha256` in chat and in the note |
| Large save slow `acquire` | Multi-GB loopback download/extraction | Keep the app running; cancellation stays effective, no timeout ceiling, temp files clean on cancel; ensure free disk ≥ 3× save size (live + bundle + snapshot) |

Large saves (MSS-136) need no size flag: both HTTP and `--bundle`
acquisition stream with bounded buffers and no fixed ceiling. See
[journalist-snapshot.md](journalist-snapshot.md) for disk/temp, refresh,
and Docker smoke details.

## Illustrative fixture example (not a real-save report)

The tracked example below is fixture-only: placeholder save id
`11111111-1111-1111-1111-111111111111`, `example-only` fingerprints, two
athletes where the real evidence always carries 32. Do not present fixture
content as reporting on a real save.

Article (`tools/SaveSnapshot/journalist.example/season-02-stage-01.example.md`):

```markdown
# [FIXTURE EXAMPLE — not a real-save report] Season 2 Stage 1: Example Winner takes the opener

Stage winner Example Winner (78.000 stage, 100.000 championship) led round 3
and held on. Runner-up Example Runner (76.500 stage, 77.000 championship)
trails by 23.000 championship points after one stage (evidence ref
`Stages:season=2:stage=1`). Championship gap and order come from
`SeasonStandings:season=2:stages=1:provisional`; prior champions: none at this
cutoff (omission, not inference).

Editorial (not a fact): the opener suggests a two-athlete watchlist, but that
is a prediction to revisit, not a result.
```

Notebook entry pattern (scoped, dated; unscoped sections stay out of
historical context):

```markdown
## Season 2 Stage 1 — 2026-11-04T12:00:00Z (rev 1)

- Cutoff: season 2 stage 1; evidence `superleague-stage-evidence` v1; stageChecksum `example-only`; databaseSha256 `example-only`.
- Note:
> _(fixture continuity note — example only)_
```

## Verification (what CI proves vs what a human rehearses)

Automated (`JournalArchiveTests`, no paid model, no account credentials):

```powershell
dotnet test tests/MtgSoloSports.Tests/MtgSoloSports.Tests.csproj --configuration Release --no-build --no-restore --filter "FullyQualifiedName~JournalArchive"
dotnet test tests/MtgSoloSports.Tests/MtgSoloSports.Tests.csproj --configuration Release --no-build --no-restore --filter "FullyQualifiedName~SnapshotHelper"
```

Covers archive identity, evidence/source matching, safe repeat/revision with
recovery, manual-edit preservation, save/season isolation, future-context
exclusion, restored-history conflicts, plus a deterministic two-stage +
revision + historical rehearsal (first completed stage archived, second stage
advanced with context showing the edited earlier article, explicit revision
with backup recovery, earlier historical request with every context file
inspected for future leakage; two saves with identical names, absent prior
articles, season rollover, unavailable-app/offline and conflicting evidence
after restore are exercised). Human chat rehearsal (writing quality, style
fit) is documented separately and is never claimed by an automated test.

No scheduler, model-service call, global config edit, Drive change or
external publication is introduced; the helper stays deterministic and
offline except for the loopback export download.
