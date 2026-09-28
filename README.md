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

## Standard local run

Prerequisites: .NET 10 SDK and Node.js 20+.

1. Start the backend (serves the API on localhost):

```bash
dotnet run --project src/MtgSoloSports
```

2. In a second terminal, start the frontend. The Vite server proxies `/api` to the backend:

```bash
npm install --prefix src/MtgSoloSports.Web
npm run dev --prefix src/MtgSoloSports.Web
```

3. Open the Vite URL in a desktop browser and use the Saves tab:

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

### Troubleshooting: NuGet restore with extra package sources (NU1507)

All .NET dependencies come from `nuget.org`. The repository-root `NuGet.Config`
clears inherited user/machine package sources and declares only `nuget.org`, so a
clean clone restores even if your machine has extra feeds configured (for example a
company `SD` source). You do not need to delete or disable those global sources:
both `dotnet restore MtgSoloSports.slnx --locked-mode` from the repository root
and Visual Studio solution restore pick up the repository configuration
automatically. Central Package Management, locked-mode restore, analyzers, and
warnings-as-errors stay enabled.
