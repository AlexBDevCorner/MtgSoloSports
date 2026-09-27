# Long-Run Baseline (MSS-031)

Opt-in harness, stable checksums and structural invariants for hundred/thousand-season saves,
plus measured baselines and retained limitations.

## Harness

- Test harness: `tests/MtgSoloSports.Tests/Features/Diagnostics/LongRunBenchmarkHarness.cs`.
- Opt-in test: `LongRunBenchmarkTests.OptIn_LongRun_SimulatesAndReports`.
- Normal CI runs the fast path only (validates bounds, no simulation).
- Set `MTG_LONGRUN=1` to simulate; seasons via `MTG_LONGRUN_SEASONS`
  (default 5 smoke; `100` routine; `1000` stress outside CI time limits).
- The harness chunks bulk work to respect `SimulateSeasonsHandler.MaxSeasonsPerRequest`,
  then reports per-season timings, database bytes, row counts, dashboard/profile/records
  query latency, replay latency, managed memory, stable checksum and invariants.
- Read-only probes (no payload scans): `GET /api/saves/{saveId}/diagnostics/longrun-stats`,
  `GET /api/saves/{saveId}/diagnostics/longrun-checksum`,
  `GET /api/saves/{saveId}/diagnostics/longrun-invariants`.

## Stable checksums

- `LongRunChecksumCalculator` hashes ordered round checksums, stage/season standing
  fingerprints, memberships and RNG state (invariant culture, ordinal strings).
- Per-season checksums localize divergence without comparing full datasets.
- Covered by `LongRunChecksumCalculatorTests` and `LongRunChecksumHandlerTests`,
  including fast-vs-manual equivalence.

## Structural invariants

`ValidateLongRunInvariantsHandler` checks six areas over normalized tables
(no round-payload decompression):

1. `league_sizes` — 8 leagues in Season 1, 9 after; 32 members per league.
2. `no_duplicates` — no duplicate active athlete per season; standings reference actives only.
3. `bonus_timing` — earned bonus within attainable round-plus-stage bounds; stage range valid.
4. `qualifier_counts` — 32-athlete field, 8 winners, 8 incumbents + 24 challengers, ranks 1..32.
5. `nationality_immutability` — one Type Cup type per athlete; capped nationality matches selection.
6. `cup_rotation` — Color Cup odd seasons only, Type Cup even only, never both.

Property runs (`LongRunInvariantTests`): three seeds × five global stages for
per-stage shape, plus one two-season run covering qualifier/Cups/rotation.

## Measured baseline

Machine: CI Linux runner, Release build, synthetic catalog (260 per color, 3 creature types).
Times wall-clock for `SimulateSeasons` of one season (Season 1: 8 leagues × 32 × 16 rounds;
Season 2+: plus Superleague, qualifier and alternating Cup).

| Save age | Before MSS-031 | After MSS-031 | DB bytes (after) | Rounds | StageStandings |
| --- | --- | --- | --- | --- | --- |
| Create | 6.1s | 5.0s | — | — | — |
| Season 1 | 22.0s | 10.9s | 11.6 MB | 4,096 | 8,192 |
| Season 2 | 39.4s | 13.0s | 27.1 MB | 8,704 | 17,408 |
| Season 3 | 60.8s | 17.7s | 46.9 MB | 13,312 | 26,624 |

Query latency after three seasons (read-only, no payload scans except single replay):

| Query | Latency |
| --- | --- |
| Season status (dashboard) | ~1 ms |
| Athlete profile | ~17 ms |
| Records | ~53 ms |
| Historical round replay (single payload) | ~23 ms |

Full suite: 499 tests pass (6m47s), including the new diagnostics tests.

## Bottlenecks fixed (exact, no rule changes)

1. **Bonus history window** (`AdvanceRoundHandler.SelectBonusSeasonIds` + callers in
   qualifier and all three Cup team/individual paths): per-bonus-load history bounded to
   the six contributing seasons (age 0..5). Older contributions decay to zero under
   `ScoringCalculator.ApplyDecay`, so the same contributions reach
   `BonusCalculator.EffectiveBonus`. Proven by `BonusWindowTests`.
2. **Missing athlete indexes** (migration `AddLongRunAthleteIndexes`):
   `StageStandings(SaveAthleteId, SeasonId)`, `SeasonStandings(SaveAthleteId)`,
   `SeasonMemberships(SaveAthleteId)`. Pure query plan change; sporting results untouched.
3. **Projection query storm** (`AthleteProjectionUpdater.RebuildBatchAsync`, 64-athlete
   chunks): seasons/leagues/memberships/standings/summaries/careers loaded once per batch
   instead of once per athlete, computing identical rows with the same pure
   `BuildSeasonSummary`/`BuildCareer` functions. Proven by
   `ProjectionBatchEquivalenceTests` plus the unchanged full suite
   (fast-vs-manual sporting equivalence still holds).

## Retained limitations

- Storage grows ~15 MB/season (Brotli round payloads, ~3.2 KB/round): ~1.5 GB at
  100 seasons, ~15 GB at 1,000 seasons in one SQLite file. The 1,000-season stress
  scenario runs outside normal CI time limits and needs disk headroom; no archival/
  compaction is introduced here.
- `RefreshAfterSeason`/`RebuildAll` still reloads full history per season finalization
  (fewer round-trips now, same data volume): total bulk cost is quadratic in seasons,
  with a ~3× smaller constant after this task. Per-stage cost growth is flattened by
  the bonus window; per-season finalization remains the long-run tail.
- Color Cup selection still loads all seasons up to the source season for
  performance/prestige inputs (once per odd season, amortized). Windowing it further
  needs aggregated prestige queries and is left as follow-up work.
- Type Cup allocation is a deterministic matching step whose cost depends on catalog
  type diversity; single-type catalogs cannot field two teams (pre-existing rule,
  surfaced during benchmarking).
- Query latencies above are three-season numbers; records/profile latencies grow with
  normalized table size (no payload scans) and should be re-measured at 100 seasons
  via the opt-in harness.
