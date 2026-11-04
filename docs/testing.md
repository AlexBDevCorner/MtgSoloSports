# Testing guide (MSS-122)

How to add backend tests without re-introducing the ~21-minute suite this task
reduced. Read this before adding any test that creates a save.

## Choose the smallest valid test

| Subject | Where | Starting state |
|---|---|---|
| Pure sporting math, RNG, selection, scoring | `SimulationKernel` unit test, no I/O | Construct kernel inputs directly |
| Handler/database behavior for one event (step vs bulk, conflict, golden) | Feature handler test with a database | Fork the cheapest prepared save that already contains the prerequisite history |
| HTTP contract (status codes, shapes, headers) | API test through `WebApplicationFactory` | Seed the factory's saves root from a prepared template; only creation/import/simulation endpoints may simulate over HTTP as their subject |
| End-to-end lifecycle proof (rotation, full S1→S3, fast-vs-manual) | Lifecycle integration test | Fresh `CreateAsync` plus real handlers; at least one from-scratch path per critical transition must stay in normal CI |

Rule: if the expensive history (a full season, a qualifier, a cup) is only
setup for your assertion, fork it. If that history *is* your assertion, simulate
it for real and say so in the test comment.

## Prepared states (all built once per test process, forked per test)

All live in `tests/MtgSoloSports.Tests/Features/SharedSaveTemplates.cs` and are
built through the same production handlers as the game. Every fork is an
independent temp root with the same save id; mutating a fork never touches the
source template. Cleanup is the consumer's job (`DeleteRoot(root)` in
`finally`).

| Fork helper | Sporting boundary | Seed / provenance | Next legal action |
|---|---|---|---|
| `ForkSeason1CompleteAsync` | Season 1, 32 stages via fast path | 4242/777, authentic | `ResolveInauguralMovement` |
| `ForkColorCupSelectedAsync` | Season 1 complete + Color Cup field selected | 4242/777, authentic (derives Season1Complete) | `RunColorCupIndividual` |
| `ForkQualifierPreResolveAsync` | S1 + inaugural + synthetic S2 feeders/standings | 4242/777, synthetic S2 (explicit) | `ResolveAutomaticMovement` |
| `ForkQualifierResolvedAsync` | Pre-resolve + movement resolved | 4242/777, synthetic S2 | `RunQualifier` (single, 16 rounds) |
| `ForkSeason1PlusCupsAsync` | S1 + full postseason via `SimulateSeasons(1)` | 4242/777, authentic | Season 2 in progress |
| `ForkSeason2CompleteAsync` | S1 postseason + real S2 32 stages via `AdvanceToNextEvent` | 4242/777, authentic | `ResolveAutomaticMovement` |
| `ForkSeason2QualifierPendingAsync` | Season2Complete + movement resolved | 4242/777, authentic (no synthetic rows) | `RunQualifier` (17 events, 272 rounds) |
| `ForkTypeCupSelectedAsync` | Fresh tournament save (64 types × 8 colors × 4) through S1+S2+qualifier+rebalance + Type Cup selection | 79079/80079, authentic tournament catalog | `RunTypeCupTeam` (source season 2) |

Seed-specific golden tests keep their own builds and pinned seeds/checksums;
do not "optimize" them onto a canonical template.

## Examples (adapted from working tests)

Pure kernel test (no save, milliseconds):

```csharp
// SimulationKernel scoring: construct inputs directly, no database.
Bonus bonus = Bonus.FromThousandths(100);
bonus.Thousandths.ShouldBe(100);
```

Template-backed feature test with safe cleanup:

```csharp
[Fact]
public async Task PartialCompletion_IsAccuratelyRepresented()
{
    // Incidental S1+S2 history is forked; the qualifier steps below still execute.
    var (store, root, saveId) = await SharedSaveTemplates.ForkSeason2QualifierPendingAsync("mtgsolosports-mss075-partial-");
    try
    {
        await new RunQualifierHandler(store).HandleAsync(saveId);
        // ... assertions on the fork only ...
    }
    finally
    {
        PostseasonTestSaves.DeleteRoot(root);
    }
}
```

HTTP read/contract test using seeded data:

```csharp
var (factory, root) = CreateFactory();
string savesRoot = Path.Combine(root, "saves");
Directory.CreateDirectory(savesRoot);
try
{
    // Catalog import + full-season simulation are incidental: seed them.
    Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(savesRoot);
    using HttpClient client = factory.CreateClient();
    using HttpResponseMessage response = await client.GetAsync($"/api/saves/{saveId:D}/cups/color/editions/1/report?format=json");
    response.StatusCode.ShouldBe(HttpStatusCode.OK);
}
finally
{
    factory.Dispose();
    TestSaveStores.DeleteRoot(root);
}
```

Real lifecycle proof that must keep its simulation:

```csharp
[Fact]
public async Task AdvanceToNextEvent_FullLifecycle_Season1_To_Season3_Start()
{
    // From-scratch proof: creating the universe and driving S1→S3 IS the subject.
    var (store, root) = CreateStore();
    try
    {
        SaveStore.CreationRecord created = await store.CreateAsync("Lifecycle Full", 707UL, 808UL, UniverseTestCatalog.Build());
        await RunSeasonOneToSeasonTwoAsync(store, created.Detail.SaveId);
        await RunSeasonTwoToSeasonThreeAsync(store, created.Detail.SaveId);
    }
    finally
    {
        TestSaveStores.DeleteRoot(root);
    }
}
```

## Add or extend a template safely

1. Build through production handlers only (`CreateAsync`, `CompleteSeasonHandler`,
   `AdvanceToNextEventHandler`, `ResolveAutomaticMovementHandler`,
   `RunAllQualifiersHandler`, `SelectTypeCupTeamsHandler`, ...). Never insert
   synthetic rows for a state whose history is asserted, except the documented
   synthetic qualifier fixtures for focused tests.
2. Chain from the cheapest existing template that already contains your prefix
   (e.g. qualifier-pending derives Season2Complete, which derives
   Season1Complete). Do not rebuild a full season inside a new builder.
3. Assert the boundary before publishing (legal next action, phase, counts).
   Throw on mismatch; failed construction must not publish a partial template
   (the existing `GetOrBuildAsync` only publishes on success).
4. Reuse the per-key `SemaphoreSlim` gate pattern; never hold a global lock
   across a whole test, share live contexts/connections, or copy a database
   while its WAL is uncheckpointed (use `TestSaveStores.CopySaveFileLockedAsync`).
5. Keep cache keys explicit and bounded. Do not build a generic fixture
   framework, a seed matrix, or checked-in SQLite snapshots: templates are
   generated for the current test process, not committed files.
6. Forks keep the same save id in isolated roots. Tests mutate only their fork
   and delete their root in `finally`. Parallel first access is safe; verify
   with a concurrent-fork test when adding a template.

## Author / reviewer checklist

- [ ] What is the smallest starting state that makes the assertion meaningful?
- [ ] Searched `SharedSaveTemplates` for an existing state before adding setup?
- [ ] A new full-season/multi-season run is justified in the test comment?
- [ ] The named action under test still executes (template did not precompute it)?
- [ ] Regression trigger, seeds, RNG expectations and assertions preserved?
- [ ] No wall-clock, network, or external MTG dependency in simulation tests?
- [ ] Ran the changed class alone from a fresh process?
- [ ] Inspected timing impact (`--logger trx` + `scripts/test-timing.py --top 20`)?
- [ ] New avoidable full-season setup is rejected during review.

## Commands

```bash
# Normal verification (backend + format + frontend + casing).
dotnet restore MtgSoloSports.slnx --locked-mode
dotnet build MtgSoloSports.slnx --configuration Release --no-restore
dotnet test MtgSoloSports.slnx --configuration Release --no-build --no-restore --logger "trx;LogFileName=timing.trx"
python3 scripts/test-timing.py tests/MtgSoloSports.Tests/TestResults/timing.trx --top 20
dotnet format MtgSoloSports.slnx --verify-no-changes --no-restore

# One class / one test from a fresh process.
dotnet test tests/MtgSoloSports.Tests/MtgSoloSports.Tests.csproj --configuration Release --no-build --no-restore --filter "FullyQualifiedName~Mss075FullCompletionTests" --logger "trx;LogFileName=qualifier-class.trx"

# Timing script self-check (also runs in CI).
python3 scripts/test-timing.py --self-check

# Opt-in diagnostics (early return without the flag does not verify them).
MTG_LONGRUN=1 MTG_CUP_CALIBRATION=1 dotnet test tests/MtgSoloSports.Tests/MtgSoloSports.Tests.csproj --configuration Release --no-build --no-restore --filter "FullyQualifiedName~LongRun|FullyQualifiedName~CupSelectionCalibration"
```

Suite budgets (measured guidance, not hard thresholds): focused class feedback
in ~1–3 minutes; full backend suite is the required PR check (see
`docs/test-performance-baseline.md` for the dated MSS-122 measurements).
Investigate regressions by ranking the slowest cases/classes in the TRX, not by
asserting millisecond thresholds in correctness tests. TRX durations include
parallel contention, so treat single-test times as ranking signals.
