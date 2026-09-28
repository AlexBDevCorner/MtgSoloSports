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

### Troubleshooting: NuGet restore with extra package sources (NU1507)

All .NET dependencies come from `nuget.org`. The repository-root `NuGet.Config`
clears inherited user/machine package sources and declares only `nuget.org`, so a
clean clone restores even if your machine has extra feeds configured (for example a
company `SD` source). You do not need to delete or disable those global sources:
both `dotnet restore MtgSoloSports.slnx --locked-mode` from the repository root
and Visual Studio solution restore pick up the repository configuration
automatically. Central Package Management, locked-mode restore, analyzers, and
warnings-as-errors stay enabled.
