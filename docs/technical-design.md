# Technical Design v1

## 1. Architectural style

MtgSoloSports is a **vertical-slice modular monolith**. It is intentionally not split into horizontal Domain/Application/Infrastructure projects.

A coding agent implementing one use case should normally find the endpoint, contracts, handler, feature-specific query/persistence code and tests together under one feature directory. Shared abstractions are introduced only after genuine cross-feature reuse.

**MediatR is explicitly prohibited.** The standard control flow is direct `Endpoint -> Handler`.

## 2. Technology baseline

- .NET 10 / ASP.NET Core Minimal APIs
- React + TypeScript + Vite
- SQLite via EF Core (introduced by implementation task)
- System.Text.Json
- localhost/browser; no desktop shell
- no auth, accounts, cloud, multiplayer or remote database

Production/local packaging serves the built React UI from ASP.NET Core. During development Vite proxies `/api` to the backend.

## 3. Backend organization

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
    Random/
    Scoring/
    Bonus/
    TieBreaking/
    Rules/
    Invariants/
  Persistence/
  Shared/
```

A typical slice:

```text
Features/Superleague/RunQualifier/
  Endpoint.cs
  Request.cs
  Response.cs
  Handler.cs
  QualifierResult.cs
  Persistence.cs
```

A slice may use EF Core directly. Do not create generic repositories, generic service layers, UnitOfWork abstractions or framework-driven CQRS.

## 4. Simulation kernel

`SimulationKernel` contains deterministic pure sporting mathematics and does not reference HTTP, EF Core, SQLite, filesystem, React, clock or network.

Core operations include round scoring, stage ranking, bonus/decay, tie-breaking, Cup selection/allocation and invariant checks.

## 5. RNG

Do not persist the behavior of `System.Random` as part of a save format. Implement a small explicit versioned deterministic PRNG, e.g. `Pcg32V1`, and persist algorithm + state.

No feature may use `Random.Shared`, create ad-hoc `Random`, shuffle by GUID, use current time, or rely on database row order for sporting randomness.

RNG state and generated result are committed in the same database transaction.

## 6. Fixed-point math

Do not use `double`/`float` for sporting calculations.

Recommended representation:

- bonus in thousandths (`100 == 0.100`);
- points in thousandths (`77 points == 77_000`).

Example: `77_000 * 1370 / 1000 = 105_490` for a 0.370 bonus.

This keeps simulation deterministic across runtime/database behavior.

## 7. Versioned rules snapshot

Each save owns an immutable rules snapshot containing at least league size, stages/rounds, scoring table, bonus tables, Superleague bonus multiplier, bonus decay and Cup selection weights.

Application defaults may evolve, but existing saves do not silently change sporting mathematics.

## 8. Storage model

Each save is a separate SQLite database file. A separate application catalog database may hold imported global MTG metadata.

The save copies the metadata required for its 2,048 selected athletes so future catalog changes cannot rewrite history.

Important durable concepts:

- SaveMetadata / RulesSnapshot
- Athlete / AthleteCreatureType
- Season / Competition / SeasonMembership
- Stage / StageStanding / SeasonStanding
- Round (one row per round with detailed compact payload)
- AthleteSeasonBonus
- AthleteSeasonSummary / career projections
- Movement
- CupTeam / CupAppearance
- Honour
- StoryEvent
- SimulationOperation

## 9. Historical round storage

Do **not** create one SQL row per athlete placement. A normal post-Season-1 year already contains roughly 147k league placements.

Persist one `Round` row plus a compact immutable compressed payload containing each athlete's shuffle position, base points, active bonus, final points, cumulative before/after score and rank movement. JSON + Brotli is acceptable for v1.

Frequently queried aggregates/standings/career totals stay normalized so ordinary statistics never need to decompress raw round history.

## 10. Bonus persistence

Aggregate bonus per athlete per season rather than persisting every +0.01 contribution as its own row. Current-stage earned bonus remains pending until the next-stage activation boundary. Only the most recent five seasons contribute to effective bonus under v1 decay.

## 11. State machine and commands

The backend owns legal progression. Representative states/transitions:

`SeasonInProgress -> StageCompleted -> NextStage/SeasonCompleted -> SuperleagueMovement -> Qualifier -> Rebalance -> Cup -> SeasonFinalization -> NextSeason`

Season 1 has a special inaugural-Superleague transition.

Canonical commands:

- AdvanceRound
- CompleteCurrentStage
- CompleteStageForAllLeagues
- AdvanceToNextEvent
- CompleteSeason
- SimulateSeasons

Higher-level commands must be behaviorally equivalent to executing the same underlying lower-level operations one by one.

## 12. Transactions and concurrency

Interactive round: one transaction. Fast stage: all 16 rounds + stage standing + bonus award in one transaction. Bulk simulation commits at safe stage/postseason boundaries.

Only one mutating simulation operation may run per save at a time (simple in-process per-save lock). Read-only queries may run concurrently.

## 13. Operation journal and recovery

Every mutating command records a `SimulationOperation` with operation type, state boundary, rules version and RNG before/after. This is diagnostic/recovery data, not user-facing undo.

SQLite transactions protect against crash partial writes. A technical checkpoint may be added for large operations/migrations; normal gameplay does not rewind sporting results.

Save portability and technical checkpoints (MSS-030):

- A save exports as a portable ZIP artifact containing the SQLite save database (`save.db`) plus `manifest.json` version metadata (save id, schema/rules versions, RNG algorithm/version, checksum). The artwork cache is not embedded.
- Import validates archive shape, checksum, identity, and schema/rules compatibility in staging before touching live files, migrates older database schemas forward on the staged copy, and never silently overwrites another save (an existing save id requires explicit overwrite, which first creates a verified checkpoint).
- Technical recovery checkpoints (`{savesRoot}/checkpoints/{saveId}/{checkpointId}.db` plus a checksum/identity sidecar) are created and verified before destructive schema migrations, import overwrites, and for large operations; restore re-verifies the checkpoint, backs up current state first, and is technical crash/migration recovery, not gameplay rewind. The normal UI exposes export/import only and never checkpoint restore.
- Database-schema migration (EF Core tables, `SaveSchemaMigrator`) and game-rule migration (sporting mathematics, `SaveRulesCompatibility`) are separate: schema migration proves the rules snapshot is byte-identical before/after and rolls back to the verified checkpoint on failure.

## 14. Invariants

Cheap structural invariants run before mutation commit, including league sizes, no duplicate active athlete, no active athlete in pool, qualifier counts, valid season phase and valid color/nationality rules.

Invariant failure aborts. Never silently repair impossible sporting state.

## 15. Superleague/rebalancing slices

Keep postseason movement split into focused slices such as:

- ResolveAutomaticMovement
- RunQualifier
- BuildNextSeasonRoster
- RebalanceFeederLeague

The core identity is always `16 safe + 8 champions + 8 qualifier = 32`.

## 16. Color Cup selection

Calculate the 35/30/25/10 selection formula with fixed-point normalized values. Recent form uses the most recent ten league stages with simple increasing recency weights 1..10. Career-prestige constants belong in the save rules snapshot and can be calibrated before rules v1 is frozen for production saves.

## 17. Type Cup allocation

Treat team formation as a deterministic matching problem because multi-type athletes can collide across teams. Capped athletes can only represent their permanent nationality. Uncapped athletes prefer the type where they have the stronger relative rank. Allocation should maximize the number of valid four-athlete teams while respecting nationality and deterministic tie-breaking.

## 18. Story events and projections

Simulation slices emit structured story events; wording is rendered separately. Examples include first stage win, first Superleague appearance, title streak, relegation, comeback, Cup selection and new record.

Maintain query projections for career totals/records transactionally. Historical tables/payloads remain sufficient to audit/rebuild projections.

## 19. Frontend architecture

React uses matching vertical feature folders: dashboard, league standings, live round, athlete profile, Superleague, Cups, history, records, save management.

Live simulation flow:

`click -> backend fully simulates/persists -> immutable presentation payload -> React reveal animation`.

Animation speed/pause/highlight state is presentation-only and never persisted as sporting state.

Fast simulation skips animation DTO work but must consume the identical sporting RNG/math sequence.

## 20. Long-run targets

The data model must support hundreds and potentially thousands of seasons. Validate with explicit long-run tests/benchmarks rather than speculative optimization.

## 21. Repository quality gates

Repository baseline:

- `Directory.Build.props`
- Central Package Management
- numeric analyzer level
- warnings as errors
- `.editorconfig`
- small curated analyzers
- package lock files once dependencies are introduced
- deterministic unit/feature/invariant/golden-simulation tests

Turn mechanical requirements into build failures when practical. Keep subjective architecture judgment in review rather than installing huge rule packs.

## 22. Architectural success criteria

The architecture succeeds when a feature usually fits one feature folder plus mirrored tests/UI, identical seeds reproduce results, animation cannot change sporting outcomes, exact old rounds can be replayed, common statistics do not scan raw history, corrupted state fails before commit, and save upgrades do not silently change sporting rules.
