# MtgSoloSports

A private nostalgia project that turns Magic: The Gathering creature cards into athletes in a persistent self-running sports universe.

The user does not manage a team. The application simulates leagues, promotion/relegation, cups, careers, records, rivalries and long-term history, with an optional card-by-card animated reveal for individual rounds.

## Current status

Repository skeleton and design documentation are initialized. Autonomous implementation remains intentionally paused until the required secrets, GitHub App permissions, and control-repo activation are ready.

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

## Local development

Backend:

```bash
dotnet run --project src/MtgSoloSports
```

Frontend:

```bash
npm install --prefix src/MtgSoloSports.Web
npm run dev --prefix src/MtgSoloSports.Web
```

The Vite development server proxies `/api` to the local ASP.NET Core host.
