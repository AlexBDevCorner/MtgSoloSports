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
- Wall clock: **15 m 37 s** (first optimized full pass).
- Sequential-equivalent: **3260 s (54 min)** — a **57% reduction** in
  sequential work and **~54% reduction** in wall clock, exceeding the 50%
  target.
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
