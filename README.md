# MtgSoloSports

A private nostalgia project that turns Magic: The Gathering creature cards into athletes in a persistent self-running sports universe.

The user does not manage a team. The application simulates leagues, promotion/relegation, cups, careers, records, rivalries and long-term history, with an optional card-by-card animated reveal for individual rounds.

## Current status

Local single-user sports simulation with persistent universes, deterministic simulation,
animated round reveal, Cups, history, records and Hall of Fame.

## Architecture

- .NET 10 / ASP.NET Core local host
- React + TypeScript + Vite UI
- SQLite persistence (to be added by implementation tasks)
- Vertical-slice modular monolith
- Direct `Endpoint -> Handler` flow
- **No MediatR**
- Deterministic simulation kernel with fixed-point arithmetic
- One SQLite file per save

See:

- [Game rules](docs/game-rules.md)
- [Technical design](docs/technical-design.md)
- [Autonomous development](docs/autonomous-development.md)
- [Implementation roadmap](docs/implementation-roadmap.md)

## Recommended local run (Docker)

Prerequisites:

- Linux: Docker Engine plus the Compose plugin (`docker compose version` works).
- Windows: Docker Desktop with **Linux containers** and the WSL2 backend enabled.

The default mode builds Linux images from a copy of the source and serves the
compiled React UI and the API from one container. No host .NET SDK or Node.js
installation is required.

> Windows checkout placement matters. Clone the repository **inside the WSL2
> Linux filesystem** (for example `~/src/MtgSoloSports`), not under
> `/mnt/c/...`. NTFS bind mounts keep Windows case-insensitivity, so a
> Windows-side checkout can hide import-casing bugs that break the Linux
> image build. The default mode below copies source into image layers (it
> never bind-mounts host source, `node_modules` or build output), but your
> checkout still needs exact-case files — run
> `python3 scripts/check-casing.py` if you suspect a mixed tree.

1. Start everything (builds the Linux images on first run):

```bash
docker compose up --build
```

2. Open http://localhost:8080 in a desktop browser and continue with
   [First-time onboarding](#first-time-onboarding-scryfall-catalog--first-universe)
   below (Scryfall import, universe creation, Dashboard/Live/History and more).

Useful commands from the repository root:

```bash
docker compose up --build -d   # same startup, detached
docker compose logs -f app     # follow backend logs (Ctrl+C stops following)
docker compose ps              # container status
docker compose up --build      # restart after source changes (rebuilds images)
docker compose down            # stop containers; saves + catalog are KEPT
docker compose down -v         # DESTRUCTIVE: deletes the saves + catalog volume
```

Persistence: per-save SQLite files live at `/data/saves` and the shared
catalog at `/data/catalog/catalog.db` inside the named Docker volume
`mtgsolosports-data`. `docker compose down`, container rebuilds and image
rebuilds preserve cards and existing universes; only
`docker compose down -v` removes them. User data and credentials are never
baked into the image.

Validating a local run (same checks CI performs):

```bash
docker compose config -q
docker compose up -d --build
sh scripts/docker-smoke.sh http://localhost:8080 180
```

The smoke script polls `/api/health` with retries (no arbitrary sleeps),
checks the public `GET /api/catalog/stats` endpoint (needs no imported
catalog or created save), and confirms a save-scoped deep link serves the
React shell via the SPA fallback.

### Optional live frontend reload (dev profile)

For fast React iteration without rebuilding the image on every edit, an
opt-in Vite dev server with hot-module replacement is available. The backend
(`app`) still runs from the built Linux image, so backend changes still need
`docker compose up --build`; only the frontend refreshes live.

```bash
docker compose -f compose.yaml -f compose.dev.yaml --profile dev up --build
```

Then open the Vite URL http://localhost:5173 in the browser. The Vite
container proxies `/api` to `http://app:8080` via Compose service DNS, so
browsers keep using localhost while containers talk service-to-service.

Requirements and tradeoffs for this mode:

- The repository **must** be checked out inside the WSL2 Linux filesystem
  (for example `~/src/MtgSoloSports`, not `/mnt/c/...`) on Windows. HMR
  bind-mounts the source tree, and NTFS mounts do not guarantee Linux case
  sensitivity — they can hide the exact import-casing bugs that
  `scripts/check-casing.py` and Linux CI are designed to catch.
- Container `node_modules` are isolated from any host install via a dedicated
  volume, so host dependencies can never shadow the Linux module graph.
- If you cannot meet the WSL-native checkout requirement, skip this file and
  use the reliable default loop instead: edit locally, then
  `docker compose up --build`.

What containerization does **not** do: building/running in Linux surfaces and
prevents environment differences, but it does not silently correct bad
imports and cannot recover case-only files already lost by a
case-insensitive checkout. A Linux image built from a collapsed Windows tree
inherits the damage. The safeguards are exact-case source, the
`scripts/check-casing.py` CI guard, and the existing Linux frontend
`typecheck`/`build` gates — never clearing the Vite cache indiscriminately
or flipping `git config core.ignorecase` as a supposed universal remedy.

## First-time onboarding (Scryfall catalog + first universe)

Works identically in Docker (`http://localhost:8080`) and host
(`http://localhost:5173`) modes. Open the app URL in a desktop browser and
use the Saves tab:

- Click **Import cards from Scryfall** on the Saves screen (also shown in the catalog
  banner when the catalog is empty or incomplete) and wait for verified quota:
  the backend discovers the current `default_cards` bulk file from
  `https://api.scryfall.com/bulk-data`, downloads the advertised `.jsonl.gz`,
  keeps eligible creature cards, excludes tokens, groups printings by card name,
  and only replaces a healthy catalog after quotas verify. The UI refreshes
  `/api/catalog/stats` automatically, shows the unique-athlete count with the
  per-color `count/256` breakdown, and reports **Ready to create a universe**
  once all eight colors reach 256. An internet connection is required; existing
  saves keep their own snapshots and are never modified.
  Card data: Scryfall bulk data — https://scryfall.com/docs/api/bulk-data.
- Create a universe on the Saves tab. Leave the advanced seed blank for a random world.
- Open the Dashboard for season/stage, leaders, recent champions, Cup context and records.
- Use Live event for the current stage rounds with instant or animated reveal, then
  History for exact replays, Cups for Color/Type Cup fields and results, and
  Records / HoF plus athlete profiles for careers, honours, movements and selections.
- Portability lives on the Saves tab: export downloads a `.mtgsave.zip` bundle and
  import restores it without touching other saves.
- Catalog fallback (advanced/offline): `POST /api/catalog/import` still accepts a
  caller-supplied Scryfall bulk JSON array for offline use. Prefer the one-click
  Scryfall button for normal onboarding.

Sporting simulation is deterministic (versioned RNG, fixed-point integers) and the UI
only replays persisted results; refreshing or changing reveal speed never resimulates.

## Alternative local run without Docker

The original host-based workflow still works and needs .NET 10 SDK and
Node.js 20+ installed locally:

1. Start the backend (serves the API on localhost):

```bash
dotnet run --project src/MtgSoloSports
```

2. In a second terminal, start the frontend. The Vite server proxies `/api` to the backend:

```bash
npm install --prefix src/MtgSoloSports.Web
npm run dev --prefix src/MtgSoloSports.Web
```

3. Open the Vite URL in a desktop browser and follow
   [First-time onboarding](#first-time-onboarding-scryfall-catalog--first-universe)
   above.

### Troubleshooting: blank page with a `RevealBoard` missing-export error

If the browser shows a completely blank page and the Vite console reports:

```text
Uncaught SyntaxError: The requested module '/src/features/reveal/revealBoard.ts'
does not provide an export named 'RevealBoard' (at RoundReveal.tsx:3:10)
```

the app failed to resolve the reveal-board component module. A separate
`/favicon.ico` 404 in the same console is unrelated and can be ignored.

Stale local files versus a committed regression: on a healthy checkout line 3 of
`src/MtgSoloSports.Web/src/features/reveal/RoundReveal.tsx` reads
`import { RevealBoard } from './RevealBoard';` (capital-R component module).
`RevealBoard.tsx` owns the React component; lowercase `revealBoard.ts` owns only
pure helpers (`boardColumnCount`, `tileMovementKind`, `tileMovementGlyph`,
`describeTile`) and never exports a component. If your line 3 points at lowercase
`./revealBoard`, your working tree predates the fix, has mixed files, or is
serving a stale Vite transform — check `git status --short --branch`,
`git log --oneline -3`, and `git diff` before assuming `main` is broken.
A genuine committed regression is caught by CI: `npm run typecheck` and
`npm run build` both fail with
`error TS2305: Module '"./revealBoard"' has no exported member 'RevealBoard'`.

Safe recovery (never deletes uncommitted work):

1. `git status --short --branch` and `git log --oneline -3` to see where you are.
2. `git fetch origin`, then `git status` to see whether you are behind `origin/main`.
3. Update without discarding edits: `git pull --ff-only` (or `git fetch origin`
   followed by `git merge --ff-only origin/main`). If it refuses because of local
   changes, commit or stash first — never `git reset --hard` or `git checkout -- .`.
4. `npm ci --prefix src/MtgSoloSports.Web` so dependencies match the lockfile.
5. Stop the running Vite server (Ctrl+C in its terminal), then
   `npm run dev --prefix src/MtgSoloSports.Web` and hard-refresh the browser.
6. Only if the error persists after the steps above, stop Vite and drop its
   transform cache with `rm -rf src/MtgSoloSports.Web/node_modules/.vite`,
   then start Vite again.

### Troubleshooting: Colorless league contains colored cards (MSS-039, fixed)

Affected versions imported multi-face Scryfall cards by reading only
`card_faces[0].colors` and treating a missing field as empty (`Colorless`).
For `split`/`flip`/`adventure`/`prepare` layouts Scryfall omits face-level
`colors` while the top-level `colors` carries the front-face color, so cards
such as **Defacing Duskmage // Vandal's Edit** (Multicolor),
**Honorbound Page** (White) and **Sasaya, Orochi Ascendant** (Green) were
misclassified into Colorless. Legitimate Colorless entries (Devoid creatures
such as Brood Butcher / Kozilek's Sentinel, Prototype artifacts such as Arcane
Proxy / Phyrexian Fleshgorger, ordinary colorless artifacts) were and remain
Colorless.

The fix resolves printed colors with layout-specific Scryfall semantics
(per-face colors for `transform`/`modal_dfc`/double-sided; top-level colors
for single-sided multi-part cards), unions `color_indicator`, and **rejects**
(a new `skippedAmbiguousColor` import counter) any creature printing whose
colors are absent on both levels instead of defaulting it to Colorless.
A malformed first printing can no longer win duplicate collapse over a later
valid printing.

Recovery:

1. Refresh the catalog: Saves tab → **Import cards from Scryfall** (or
   `POST /api/catalog/import` for offline bulk JSON). Both paths share the
   corrected normalization.
2. Confirm: `/api/catalog/stats` per-color `count/256` breakdown, and the
   import response `skippedAmbiguousColor` (ambiguous creature printings
   skipped, not hidden inside Colorless).
3. Create a **new** universe after the refresh. New saves draw Colorless only
   from genuinely colorless athletes under `docs/game-rules.md`.

Existing saves are **unchanged**: a catalog refresh replaces only the shared
catalog; saves keep their own athlete snapshots, leagues, round results,
history, bonuses and immutable payloads. Refreshing does **not** repair
universes already created with bad snapshots — finish or discard those saves
and start a new universe for correctly classified leagues.

### Troubleshooting: NuGet restore with extra package sources (NU1507)

All .NET dependencies come from `nuget.org`. The repository-root `NuGet.Config`
clears inherited user/machine package sources and declares only `nuget.org`, so a
clean clone restores even if your machine has extra feeds configured (for example a
company `SD` source). You do not need to delete or disable those global sources:
both `dotnet restore MtgSoloSports.slnx --locked-mode` from the repository root
and Visual Studio solution restore pick up the repository configuration
automatically. Central Package Management, locked-mode restore, analyzers, and
warnings-as-errors stay enabled.
