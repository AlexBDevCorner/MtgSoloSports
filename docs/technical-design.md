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

- bonus in thousandths of a percent (`100 == 0.100%`);
- points in thousandths (`77 points == 77_000`).

Example: `77_000 * 100_370 / 100_000 = 77_284` for a 0.370% bonus (truncated to thousandths).

This keeps simulation deterministic across runtime/database behavior.

## 7. Versioned rules snapshot

Each save owns an immutable rules snapshot containing at least league size, stages/rounds, scoring table, bonus tables, Superleague bonus multiplier, bonus decay and Cup selection weights.

Rules v2 (tiered feeder model) keeps the same sporting core and replaces the single integer Superleague multiplier with an explicit per-tier rational scale (Superleague 2/1, Feeder 1 1/1, Feeder 2 1/2, Feeder 3 1/4) stored in the snapshot. v1 snapshots stay readable through the v1 compatibility path; new saves use v2. League rows carry an explicit feeder division (None/First/Second/Third) with a unique index on (season, kind, division, color) so tiers never depend on league-name parsing. New tiered saves start Season 1 with 24 feeders (F1/F2/F3 per color, 96 active / 160 pool per color, 768 active globally); the inaugural Superleague still forms from F1 only (8 × 4 = 32) with F2/F3 carrying over. Existing v1 saves (8 feeders, 256 active) stay openable and enter tiered rules only via the explicit v1→tiered sporting upgrade at a CupComplete boundary, which persists new rules, F2/F3 leagues, memberships, RNG-after and 512 TierUpgradeSeed movements atomically and is idempotent across retry.

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

### Postseason events played round by round

The qualifier (`qualifier`, 32 × 16 rounds), Color Cup individual
(`color-cup-individual`, 32 × 16), Color Cup team (`color-cup-team`, 4 groups ×
8 rounds) and Type Cup team (`type-cup-team`, 4 groups × 8 rounds) can be
played one round at a time, exactly like league rounds. The Type Cup team event
uses explicit tournament identity (`TournamentPhase` 0/1/2 plus
`QualificationGroup` alongside rank `GroupNumber`) so future qualification and
Final stages cannot collide; current single-field rows store legacy 0/0.
Tiered saves resolve
16 additional feeder qualifiers (8 F1↔F2 plus 8 F2↔F3, each 16 athletes × 16
rounds) as one-shot events in canonical order via `POST
…/qualifiers/{boundary}/{color}` or together via `POST …/qualifiers/run-all`;
all 17 read back through `GET …/qualifiers` with boundary/color identity for
the MSS-060 overview. The Dashboard lifecycle keeps one `RunQualifier` step
for the whole phase in both modes. Round-by-round play:

- Step slices `POST …/superleague/qualifier/rounds/next`,
  `…/cups/color/individual/rounds/next`, `…/cups/color/team/rounds/next` and
  `…/cups/type/team/rounds/next` play exactly one round under the per-save
  lock and commit the round payload with the RNG-after state in one
  transaction. For team events, the step that plays a group's last round (other
  than the final group) also draws that group's leg tie-break, so the next
  group starts from the same RNG state as a one-shot run. The step that plays
  the event's final round runs the normal completion (ranking with tie-break,
  standings/results, medals and titles, next-roster application, stories,
  persisted phase) in the same transaction, deriving results from the stored
  payloads.
- The existing one-shot runners (and `AdvanceToNextEvent`/`SimulateSeasons`)
  play all remaining rounds and complete, so they resume a partly played
  event. Stepping, one-shot and mixed runs produce byte-identical payloads,
  checksums, results, stories and final RNG state (tests pin this, including
  golden values from the pre-stepping runners).
- A partly played event is a validated in-progress state: rounds contiguous in
  (group, round) order and fewer than the total, no results rows, each round's
  RNG-before equal to the previous RNG-after (or the recomputed group
  tie-break state at a group boundary), and the save RNG row equal to the
  state after the last step. Any violation aborts.
- A partly played event owns the save RNG until it completes: a step or
  one-shot run of any other postseason event is refused as a conflict while
  one is partly played, so nothing can move the RNG under it.
- Season status and `AdvanceToNextEvent` responses carry `eventProgress`
  (`event`, `sourceSeasonNumber`, `roundsPlayed`, `totalRounds`, `groupCount`,
  `roundsPerGroup`, next `group`/`roundInGroup`) when the next legal action is
  one of these events; once rounds exist the computed phase is
  `QualifierInProgress`, `ColorCupIndividualInProgress`,
  `ColorCupTeamInProgress` or `TypeCupTeamInProgress` (computed only; the
  persisted phase changes at completion). Legal actions are unchanged.
- History reads (no lock): `GET …/history/seasons/{n}/events`,
  `…/events/{event}/rounds`, `…/events/{event}/rounds/{round}?group=` (league
  replay shape) and `…/events/{event}/team-standings` (persisted final ranking,
  or a provisional display sum of stored points over every round played so far).
  With `?beforeGroup=&beforeRound=` the sum covers only the rounds ahead of that
  round; the response also lists each athlete's team, so the live view shows
  team standings that follow the round reveal (totals before the round plus the
  stored points of the athletes revealed so far).

## 16. Color Cup selection

Calculate the 35/30/25/10 selection formula with fixed-point normalized values. Effective bonus is accumulated career advantage; completed-season performance and recent form are current results adjusted by current competition strength. Recent form uses exactly the completed source season's final ten league stages with simple increasing recency weights 1..10. Career-prestige constants belong in the save rules snapshot and can be calibrated before rules v1 is frozen for production saves.

Competition strength (MSS-064, Rules v2): Superleague 1000, Feeder 1 800, Feeder 2 600, Feeder 3 400 permille, stored in every tiered snapshot and validated as Super > F1 > F2 > F3 > 0. The softer factor deliberately avoids counting tier strength three times at bonus severity. One shared pure `SimulationKernel/Cups/CupSelectionMetrics` component resolves the explicit persisted league level (never league-name parsing) and scales performance (`points × factor / 1000`) and form (final-ten weighted aggregate × factor / 1000) with checked truncation. Pool has no source-season standing and scores zero; active requires a complete final window (generalized from `StagesPerSeason`/`RecentFormStageCount`) or selection aborts. Both Color and Type Cup use the same raw calculation; normalization (per-color vs global) and 35/30/25/10 weights are unchanged.

### Selection as an event

Selection stays one saved lifecycle step (no RNG), but it is presented as an
event of its own. In the same transaction as the 32 selection rows the slice
stores one `CupSelectionReports` row (one per source season, compact
Brotli-compressed payload): per color the top 12 of the ranking with raw
inputs, normalized components and final ratings, the field size and the
weights used, plus v2 strength explanation (source league name/level or Pool,
factor, unadjusted points, adjusted performance, unadjusted form aggregate,
adjusted form). `GET /api/saves/{saveId}/cups/color/selection-report?sourceSeason=`
returns it with names and artwork; it never recomputes ratings, because
honours and bonus keep changing after the selection. Selections saved before
the table existed fall back to the selection rows (selected athletes only,
`hasFullRanking: false`). v1 report payloads remain readable (missing v2 fields
decode to defaults) and are never rewritten. The report must agree with the
selection rows or the read aborts.

## 17. Type Cup allocation

Treat team formation as a deterministic matching problem because multi-type athletes can collide across teams. Capped athletes can only represent their permanent nationality. Uncapped athletes prefer the type where they have the stronger relative rank. Allocation should maximize the number of valid four-athlete teams while respecting nationality and deterministic tie-breaking.

### Allocation as an event

`TypeCupAllocationInsight` runs the same scoring and matching as
`TypeCupAllocation.Allocate` and also returns every viable type's ranking with
where each athlete ended up. The select slice stores it in `CupSelectionReports`
alongside the selection rows: per fielded team the top 12 of the type ranking
plus the four members, each with capped state at selection time and the other
viable types it could represent; plus viable types that fielded no team.
Ratings reuse the same 35/30/25/10 strength-aware performance/form calculation as
the Color Cup via shared `CupSelectionMetrics` (normalized globally), with v2
strength explanation per candidate. `GET /api/saves/{saveId}/cups/type/selection-report?sourceSeason=` adds a
reason per member: `Capped`, `OnlyType`, `BestRank` or `Balanced` (placed away
from its best-ranked type so the most teams take part).

### Scalable tournament format (MSS-061)

The Type Cup rules snapshot carries a versioned tournament-format rule
(`TypeCupTournamentFormatVersion`: 0 legacy unbounded single-field, 1 scalable
qualification plus fixed 32-team Final; `TypeCupMaxDirectFinalTeams = 32`,
`TypeCupFinalTeamCount = 32`). New saves use format 1; historical snapshots
without the fields decode as 0 and their single-field Cups stay readable
without rewriting results.

Tournament-format math lives in pure `SimulationKernel/Cups/TypeCupTournamentFormat`:
group count `ceil(N / 32)` (0 for direct Finals), balanced sizes differing by at
most one, deterministic random draw via only `Pcg32V1` (ordinal-canonicalized
input, Fisher-Yates shuffle, contiguous balanced chunks numbered after the draw),
Final-place quotas totalling exactly 32 (larger groups first, ties by group
number), draw checksum fingerprint, and a 32-team ceiling guard so new-format
rounds never touch the legacy greater-than-32 minimum-point extension (kept only
for historical reads via `ScoringCalculator.TypeCupBasePointsForPosition`; new
fields use the strict `TypeCupBasePointsForNewFormatPosition`).

Persistence keeps selection and draw as separate concepts. `TypeCupTournamentDraws`
(one row per team) stores source season, creature type, qualification group,
group size and field metadata, Final-place quota, rules/format versions, RNG
before/after and draw checksum; the RNG commit and the draw share one
transaction and squad membership is verified unchanged. Round, leg and team
tables carry explicit `TournamentPhase` (0 legacy, 1 qualification, 2 Final) plus
`QualificationGroup` (0 outside qualification) alongside the athlete rank
`GroupNumber`, so qualification and Final rounds never collide; legacy rows store
0/0. The draw slice exposes `POST/GET /api/saves/{saveId}/cups/type/draw`.
MSS-062 runs the tournament around the existing team-event kernel: direct
Finals (1-32 teams) persist one 4x8 competition as phase 2; larger fields play
every qualification group in draw order (each 4x8, standard table, medals
None) then a fresh 32-team Final from zero (medals/honours Final-only).
Step-by-step and one-shot share one RNG chain with rank-group and stage
tie-break boundaries; qualification standings plus nationality persist per
completed group so eliminated teams retain history. Completion, lifecycle
progress and history treat the Cup as one competition complete only with Final
standings. `GET /api/saves/{saveId}/cups/type/tournament` exposes the draw,
each qualification result, the 32 finalists, the Final and the champion;
`cups/type/team` stays the convenient Final result and old phase-0 saves read
unchanged.

## 17a. Cup history read model

Three read-only slices under `Features/Cups` serve the Cups pages. None of
them touch rules, RNG or schema.

- `ListCupEditions` — `GET /api/saves/{saveId}/cups/editions`. Every edition
  that has stored squad selections, newest first, with state (`Selected`,
  `InProgress`, `Completed`), team count, team podium and the Color Cup
  individual champion; plus the all-time team table of each Cup. A Color
  edition is `Completed` only when both the team and the individual event
  have stored standings.
- `GetColorCupTeamHistory` / `GetTypeCupTeamHistory` —
  `GET /api/saves/{saveId}/cups/{color|type}/teams/{teamKey}/history`. One
  team across every edition it was selected for: honours, each season's squad
  with rating, group leg and (Color) individual result, the all-time roster and
  (Color) individual medals. Both slices load their own tables and share
  `CupHistory/CupTeamHistoryBuilder`, which aborts when a squad does not have
  the Cup's team size, a leg belongs to an athlete outside the squad, or a
  standing has no selection. The Type Cup reason comes from the stored
  selection report and is absent for editions selected before reports existed.

Team keys: Color = lower-case sporting-color name (names only, never the
numeric value); Type = the stored creature type, exact case.

Edition pages reuse the existing per-season endpoints (`cups/color/team`,
`cups/color/individual`, `cups/type/team`, `cups/*/selection-report`) with
`sourceSeason`. Their leg and individual rows now carry `imageUrl`; checksums
are unchanged.

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
