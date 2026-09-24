# AGENTS.md — MtgSoloSports

Instructions for coding agents. The AutonomousWork task specification defines **what** to build; this file defines repository-level constraints for **how** to build it.

## Project overview

MtgSoloSports is a local single-user sports simulation using MTG creature cards as athletes. It runs as an ASP.NET Core application on localhost with a React/Vite frontend and will persist independent universes in SQLite.

## Architecture — normative

1. **Vertical slices are mandatory.** Organize application code by feature/use case, not Domain/Application/Infrastructure layers.
2. **No MediatR.** Endpoints call feature handlers directly (`Endpoint -> Handler`). Do not add mediator/CQRS frameworks.
3. Do not add generic repository, generic service, UnitOfWork, message bus, microservice or distributed-cache abstractions.
4. A feature should keep endpoint/contracts/handler/feature-specific query or persistence logic close together under `Features/<Area>/<UseCase>`.
5. Shared code is allowed only for genuinely global concepts. The main deliberate shared area is `SimulationKernel`.
6. `SimulationKernel` must remain pure: no HTTP, React, EF Core, SQLite, filesystem, clock or network dependencies.
7. All sporting randomness must come from the versioned simulation RNG. Never use `Random.Shared`, `new Random()`, GUID ordering, current time or database ordering as game randomness.
8. Sporting math uses fixed-point integers. Never use `double`/`float` for points, bonus, rankings or selection scores.
9. Rules affecting sporting outcomes are stored/versioned with each save. Changing defaults must not silently change existing universes.
10. Simulation and presentation are separate. Animated reveal/replay must consume persisted immutable results and must never resimulate a round.
11. Fundamental invariant failures abort the mutation; never silently repair corrupted sporting state.

## Expected backend layout

```text
src/MtgSoloSports/
  Features/
    Saves/
    Catalog/
    Universe/
    Simulation/
    Leagues/
    Superleague/
    Cups/
    Athletes/
    History/
    Dashboard/
    Records/
  SimulationKernel/
  Persistence/
  Shared/
```

Tests mirror feature paths under `tests/MtgSoloSports.Tests/Features/...` when the test project is introduced.

## Build / verification

Repository-wide verification, once the foundation task adds the test project and lock files:

```bash
dotnet restore MtgSoloSports.slnx --locked-mode
dotnet build MtgSoloSports.slnx --configuration Release --no-restore
dotnet test MtgSoloSports.slnx --configuration Release --no-build --no-restore
dotnet format MtgSoloSports.slnx --verify-no-changes --no-restore
npm ci --prefix src/MtgSoloSports.Web
npm run typecheck --prefix src/MtgSoloSports.Web
npm run build --prefix src/MtgSoloSports.Web
```

Before lock files exist, use ordinary restore/install only for the repository-bootstrap task.

## Quality gates

- .NET 10, nullable and implicit usings are repository-wide.
- Warnings are errors.
- Keep `AnalysisLevel` numerically pinned.
- Centralize NuGet package versions in `Directory.Packages.props`.
- Use a small curated analyzer set rather than a huge opinionated rule pack.
- Add/update deterministic tests for behavior changes.
- Do not depend on wall clock, network or external MTG services in simulation tests.

## Persistence

- One SQLite database per save.
- RNG state commits in the same transaction as generated simulation results.
- Frequently queried statistics should be normalized/projection data.
- Detailed historical round replay should use immutable compact payloads rather than one database row per athlete placement.
- Database-schema migration and game-rule migration are separate concepts.

## Frontend

- Organize React by feature under `src/MtgSoloSports.Web/src/features` as functionality appears.
- Animation-only state stays in React and is never persisted as sporting state.
- Prefer local feature components; move to shared UI only after real reuse emerges.
- Dark presentation is the default direction, with sports-database screens for history and expressive card-based presentation for live rounds.

## Autonomous worker rules

- Implement exactly one dispatched task from the read-only `control/` checkout.
- Never edit `control/`, task requirements, priorities or future tasks.
- Work on `autonomous/<TASK-ID>` from `main`; reuse the same branch/PR on retries.
- Push checkpoints during long work. A successful run must leave its PR ready for review.
- PR title starts with `[<TASK-ID>]` and labels include `autonomous`, `autonomous:opencode`, `task:<TASK-ID>`.
- Never merge, force-push or inspect/reconstruct workflow credentials.
- Changes outside the dispatched vertical slice should be rare and explicitly justified.

## Documentation

The authoritative product/design context is in `docs/`. Keep docs synchronized when a task intentionally changes rules, architectural constraints or the data model.
