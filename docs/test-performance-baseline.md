# Test Performance Baseline (MSS-067)

Normal backend verification took ~34 minutes per full pass, dominated by
repeated full-season SQLite simulations. This note records the reproducible
before/after baseline, the ranked costs, what changed, and the remaining
dominant costs with concrete follow-up work.

## Commands

```bash
dotnet restore MtgSoloSports.slnx --locked-mode
dotnet build MtgSoloSports.slnx --configuration Release --no-restore
dotnet test MtgSoloSports.slnx --configuration Release --no-build --no-restore --logger "trx;LogFileName=timing.trx"
python3 scripts/test-timing.py tests/MtgSoloSports.Tests/TestResults/timing.trx --top 20
```

`scripts/test-timing.py` is the durable slow-test report: slowest individual
tests and slowest classes from any trx file, with no manual instrumentation.

## Baseline (before, MSS-067 start)

- Suite: 946 tests, all passing.
- Wall clock: **33.7 min** (`Total time: 33.7242 Minutes`).
- Sequential-equivalent (trx durations summed): **7620 s (127 min)**,
  i.e. ~3.8x parallelism on 4 cores — the suite is CPU-bound in simulation,
  EF change tracking, Brotli round payloads and per-round SHA-256
  fingerprints, not host-bound (build is ~70 s, restore is network).
- Pure `SimulationKernel` coverage (394 tests) costs **0.4 s total**: all cost
  is in file-per-save SQLite integration tests.

Environment caveats: Ubuntu 24.04, .NET SDK 10.0.401, runtime 10.0.12,
4 CPUs, 15 GiB RAM, local SSD temp dirs. Trx per-test durations include
parallel contention, so single-test numbers below are approximate and
intended for ranking, not as exact budgets. A single Season 1 simulation
(Create of the 2048-athlete universe plus 32 global stages) costs ~30-45 s
in isolation on this machine.

## Ranked costs (before)

1. **Repeated full Season 1 simulations (~70% of sequential time).**
   Universe rules fix the minimum catalog (256 per sporting color; the test
   catalog carries 260), so `CreateAsync` plus 32 stages cannot be shrunk per
   save. ~100 tests each built at least one Season 1 from scratch, and ~15
   equivalence/determinism tests built the *same seeded save twice* in one
   test (step-vs-one-shot, fast-vs-manual, same-seed determinism).
2. **Multi-season soaks in normal CI.** `FastAndManual_ThreeSeasons` (2x3
   seasons, 458 s), `ThreeSeasons_Rotate` (227 s), `TwoSeasons` (128 s),
   `Historical_OlderSeason` via `SimulateSeasons(2)` (132 s).
3. **Step-by-step lifecycle drains.** `PrepareLifecycleAsync` /
   `RunLifecycleToActionAsync` drive Season 1 (and Season 2, really) stage by
   stage with a status query per step: TransitionReveal (2x~120 s),
   EventProgress (3 tests, ~240 s combined), SeasonLifecycle Full (119 s).
4. **Per-test HTTP pipeline in API tests.** 95 API tests each imported the
   2048-card catalog, created the save and simulated seasons over HTTP
   (~2-5 s of host startup plus full simulation cost each).
5. **Long-tail single-save Season 1 tests.** Records, athletes, stories,
   dashboard, history, cup selection/run, qualifier, rebalance and inaugural
   tests each paid a full Season 1 for read-mostly assertions.

## What changed (after)

- `TestSaveStores.ForkAsync`: checkpointed WAL file-copy fork of a prepared
  save into an isolated temp root (same save id, mirroring the production
  checkpoint path). Dual-save equivalence tests build once and fork instead
  of simulating the same seeded season twice. Both sides still execute
  independently from bit-identical starting state; golden tests keep pinned
  seeds and own builds.
- `SharedSaveTemplates`: process-wide, thread-safe template cache
  (Season1Complete, ColorCupSelected, QualifierPreResolve,
  QualifierResolved, Season1PlusCups; canonical seed 4242/777) with
  per-test isolated forks. Converted ~80 single-save tests; seed-specific
  golden coverage unchanged.
- `PostseasonTestSaves.DrainToActionAsync`: lifecycle tests fork Season 1
  and keep the exact `AdvanceToNextEvent` drain path.
- Opt-in long runs: `FastAndManual_ThreeSeasons` and `TwoSeasons` run only
  with `MTG_LONGRUN=1`. Fast deterministic counterparts stay in normal CI
  (1-season fast-vs-manual equivalence x2, three `FiveGlobalStages` seeds,
  qualifier/cup/rotation integration tests).
- `Historical_OlderSeasonHoldsRecordAfterAdvancing` uses one completed
  Season 1 plus a synthetic Season 2 shell (same pattern as the sibling
  membership test) instead of `SimulateSeasons(2)`.
- API seeding: `SeedSeason1CompleteAsync` / `SeedSeason1PlusCupsAsync` copy
  a template database into the test factory's saves root, so endpoint tests
  (records, inaugural) skip catalog import and HTTP simulation while still
  exercising the endpoints under test over HTTP.
- No production behavior changed: sporting rules, RNG, mathematics,
  checksums and golden values are untouched. No meaningful coverage
  removed: every converted test runs the same handlers and asserts the same
  invariants from an equivalent starting state.

## After

- Suite: 946 tests, all passing (2 tests opt-in-gated, fast path in normal CI).
- Wall clock, three consecutive full-suite passes on the same machine:
  **15 m 37 s**, **14 m 49 s**, **15 m 27 s** (no flakes, no shared-state
  failures under default parallel execution).
- Sequential-equivalent: **3260 s (54 min)** — a **57% reduction** in
  sequential work and **~54-56% reduction** in wall clock, exceeding the 50%
  target.
- Slowest kept tests also pass individually: `ThreeSeasons_Rotate` plus
  `AdvanceToNextEvent_FullLifecycle` together in 2 m 14 s in isolation
  (trx durations are inflated ~2-3x by parallel contention; see caveats).
- Opt-in diagnostics verified with flags set
  (`MTG_LONGRUN=1 MTG_CUP_CALIBRATION=1`): `FastAndManual_ThreeSeasons`,
  `TwoSeasons_QualifierCupsAndRotationHold`, `OptIn_CupCalibration`,
  `OptIn_LongRun` all pass (9 m 45 s combined).
- Frontend verification (not a bottleneck): `npm ci`, typecheck ~7 s,
  build ~8 s, 426 node tests ~3 s.

## Remaining dominant costs (10-minute target not reached)

The suite now sits at ~15.5 min wall; a further ~35% cut would be needed for
10 minutes. The remaining costs are almost entirely simulations that *are*
the test action, and cutting them would remove end-to-end coverage:

1. `ThreeSeasons_Rotate` (~235 s seq): the only Color-Type-Color rotation
   proof over real simulations. Follow-up: keep, or split rotation-boundary
   assertions (already inspectable per season) from the full 3-season run.
2. `SeasonLifecycle FullLifecycle S1->S3` (~133 s) and
   `RejectsIllegalTransitions` (~43 s): the only from-scratch lifecycle
   paths. Follow-up: start the full-lifecycle drain from a Season 1 template
   fork once a from-scratch lifecycle test remains elsewhere (currently both
   are from scratch by design).
3. Fast-vs-manual equivalence over full seasons
   (`SimulateSeasons_Equivalent`, `FromMidSeason`, `CompleteSeason_Equivalent`,
   `LongRunChecksum FastSimulation_MatchesManual`, ~300 s combined): both
   full-season simulations are the assertion. Follow-up: none safe; these
   are the fast-path correctness proofs.
4. Lifecycle drains that really simulate Season 2 plus 272-round feeder
   qualifiers (EventProgress, TransitionReveal, ~450 s combined):
   qualifier *runs* are inherent; only the Season 1 prefix was shared.
   Follow-up: a bulk Season 2 template (real, not synthetic) if a second
   from-scratch lifecycle path stays elsewhere.
5. API end-to-end simulations whose setup *is* the endpoint under test
   (`FastSimulationApi`, `HistoryApi` fresh-save narrative,
   `CompleteSeasonApi`): keep by design. The remaining factory-based read
   tests (`ListCupEditions`, `ColorCupTeamHistory`) already share one
   3-season fixture; sharing one `WebApplicationFactory` per collection
   would additionally save host startup (~2 s per test).
6. Catalog import per API test class (2048 cards over HTTP each): share one
   imported catalog per test class via `IClassFixture` (saves host startup
   plus one import per test).

None of the above was taken because each trades unique end-to-end coverage
for time. The 50% target is met; revisit only if CI budgets demand it.

---

# MSS-122 (2026-11-02): reusable Season 2 / Type Cup states

Follow-up to MSS-067. Historical MSS-067 measurements above are preserved;
below is the fresh MSS-122 baseline and the dated result. Commands are the
same as MSS-067 (`restore --locked-mode`, `build Release --no-restore`,
`test Release --no-build --no-restore --logger "trx;LogFileName=..."`,
`python3 scripts/test-timing.py <trx> --top 20`).

## Fresh baseline (before, MSS-122 start)

- Target commit: `d5d8dee` (main at dispatch). SDK 10.0.403, runtime 10.0.14,
  Ubuntu 26.04.1 LTS, 4 CPUs, 15 GiB RAM, ephemeral runner
  (`computerName runnervmnwato` in TRX), Release, no opt-in flags.
- Restore ~2 s, build ~82 s (separate from test execution below).
- Suite: **1276 tests, all passing**. `Duration: 21 m 40 s`, wall clock
  **1304 s (21.7 min)**, sequential-equivalent **4770 s (79.5 min)**
  (~3.7x parallelism; TRX durations include parallel contention and are
  ranking signals, not exact budgets).
- Slowest 20 cases: `ThreeSeasons_Rotate` 186 s, `FullLifecycle S1→S3` 120 s,
  `TypeCupCompletionHandoff` 119/115/109 s (3 tests),
  `Mss075Full` 117 s, `Mss075RepeatSafety` 113/87 s (2 tests),
  `TypeCupPostSelection` 109 s, `EventProgress` 93/73/73 s (3 tests),
  `FastSimulation Equivalent` 90/84 s, `TransitionReveal` 78/73 s (2 tests),
  `Mss075Resume` 71 s, `Mss075Partial` 70 s, `LongRunChecksum` 56 s,
  `CupLeagueReport TypeCup` 55 s.
- Slowest classes (true `TestMethod@className` grouping): `TypeCupCompletionHandoff`
  343 s (3), `FastSimulationTests` 325 s (11), `EventProgress` 240 s (4),
  `CupLifecycleIntegration` 204 s (3), `Mss075RepeatSafety` 199 s (2),
  `StandingsStagePlacements` 180 s (13), `SeasonLifecycle` 164 s (3),
  `ColorCupTeamStep` 159 s (8), `ColorCupIndividualStep` 156 s (7),
  `TransitionReveal` 154 s (3).
- Audit: every top case except the two from-scratch lifecycle proofs and the
  fast-vs-manual equivalences paid a repeated lifecycle prefix as incidental
  setup — `ForkSeason1Complete` + `DrainToAction("RunQualifier")` (real S2
  32 stages + movement, ~40-50 s) in 10 qualifier/lifecycle tests, and
  `Create + drain to SelectTypeCup` (S1+S2+qualifier+rebalance, ~90 s) in 4
  Type Cup tournament tests — plus one Season 1 build per step-equivalence
  and read-model test still on `Prepare*` helpers.

## What changed (MSS-122)

- `SharedSaveTemplates`: added `ForkSeason2CompleteAsync` (real S2,
  movement pending), `ForkSeason2QualifierPendingAsync` (real S2, movement
  resolved, qualifier pending; chains Season2Complete), and
  `ForkTypeCupSelectedAsync` (tournament catalog, 64 types × 8 colors × 4,
  real S1+S2+qualifier+rebalance + Type Cup selection, team event pending).
  All built once per process through production handlers with boundary
  assertions; forks are isolated temp roots via the existing
  checkpoint/copy path.
- Migrated: `Mss075Full/Partial/Resume/RepeatSafety` (5 tests),
  `EventProgress` (3), `TransitionReveal` movement + rebalance (2),
  `TypeCupCompletionHandoff` (3) + `TypeCupPostSelection` (1),
  `QualifierStep`/`ColorCupTeamStep`/`ColorCupIndividualStep` equivalences (7),
  `StandingsStagePlacements` Season 1 reads (3), `ColorCupMedalTables` (2),
  `ColorCupTeamHistory`/`ListCupEditions` (2), `ColorCupIndividual/Team`
  determinism (2), `FeederQualifier` determinism (1), `Rebalance` +
  `Inaugural` determinism (2). Every named action still executes in its test;
  golden seeds/checksums keep their own builds.
- Added `SharedSaveTemplateIsolationTests` (4 tests): fork independence,
  parallel first-use agreement, Season2Complete and TypeCupSelected
  boundaries.
- `scripts/test-timing.py`: class grouping now uses TRX
  `TestMethod@className` metadata (parameterized cases group correctly) with
  display-name fallback, plus `--self-check` synthetic coverage. CI runs the
  self-check, publishes a ranked top-20 summary to the job summary, and
  uploads bounded TRX + summary artifacts (7 days, timing files only) without
  masking test failures.
- Docs: new `docs/testing.md` (selection, state inventory, examples, safe
  extension, checklist, commands), normative testing section in `AGENTS.md`,
  README link.

## After (three consecutive cold-cache processes)

- Code commit measured: `4ffd8fe` (docs-only follow-up does not affect tests).
  Suite: **1280 tests (1276 + 4 isolation), all passing, three consecutive
  processes with no isolation/order failures.**
- Run 1: `Duration: 14 m 28 s`, wall **871 s**, sequential **2951 s**.
- Run 2: `Duration: 14 m 46 s`, wall **889 s**, sequential **2886 s**.
- Run 3: `Duration: 14 m 12 s`, wall **855 s**, sequential **3043 s**.
- Median wall **871 s (14.5 min)** vs 1304 s baseline: **~33% wall, ~38%
  sequential reduction (4770 s → ~2950 s, ~1820 s saved).**
- Remaining slowest cases (run 1): `FullLifecycle S1→S3` 122 s,
  `ThreeSeasons_Rotate` 105 s, `TypeCupCompletionHandoff PartialThenBulk` 95 s,
  `FastSimulation` equivalence 84/77/61 s, `Mss075Resume` 75 s,
  `LongRunChecksum` 67 s. EventProgress/Mss075 drains are gone from the top;
  qualifier and Type Cup tournament *runs* remain because they are the named
  actions.
- Changed expensive classes pass alone from fresh processes (Mss075 family +
  EventProgress + TransitionReveal + TypeCup handoff/post-selection: 16 tests
  in 2 m 8 s; step/read-model batch: 68 tests in 2 m 36 s; isolation: 4 tests
  in 1 m 56 s; inaugural: 4 tests in 33 s).
- Opt-in diagnostics with flags set
  (`MTG_LONGRUN=1 MTG_CUP_CALIBRATION=1`): 12 tests pass in 9 m 9 s.
- Retained proofs pass: from-scratch `FullLifecycle`, golden
  `ColorCupTeam/Qualifier` one-shots, fast-vs-manual equivalence, checksum
  equivalence, API read, history replay, isolation (11 tests in 3 m 10 s).

## Target shortfall and irreducible work (Requirement 19)

The 50% goal (≤652 s wall, ideally <10 min) is **not met**; median 871 s
misses by ~219 s (~34% further cut needed). This is a measured material
reduction with no safe high-impact setup reuse left unimplemented — not a
report-only result:

1. From-scratch lifecycle proofs (`FullLifecycle S1→S3`, `ThreeSeasons_Rotate`,
   ~225 s combined per run): the simulated history is the subject. Kept by
   design with `RejectsIllegalTransitions` as the additional S1 path.
2. Fast-vs-manual/bulk equivalence (`FastSimulation` 300 s,
   `LongRunChecksum` 67-77 s): both full-season simulations are the assertion.
   Forking already avoids double universe creation; templating either side
   would precompute the compared answer.
3. Qualifier (272 rounds) and Type Cup tournament runs that remain
   (`Mss075Resume/Repeat`, `TypeCupCompletionHandoff/PostSelection`,
   ~300 s combined): the runs still execute per test by design; only their
   prefixes were shared. Precomputing them would erase the regression trigger.
4. API end-to-end simulations whose setup is the endpoint under test
   (`FastSimulationApi`, `HistoryApi` bulk, `CompleteSeasonApi`) plus per-test
   HTTP host startup (~2 s × ~100 factory tests) and catalog import: sharing
   one factory/catalog per collection would trade isolation for seconds and
   was rejected as disproportionate.
5. Long tail (Cups 221 tests, Reports 136 tests): already on templates or
   cheap single-stage setups; remaining per-test seconds are handler
   execution, not shareable seasons.

No ordinary coverage was hidden, skipped, gated, or moved out of the suite to
manufacture savings (counts rose 1276 → 1280; opt-in gates unchanged).
Reaching 10 minutes would require removing or thinning the above correctness
proofs, which this task explicitly forbids.
