# Chaos Calibration (MSS-118)

Opt-in determinism and fairness evidence for the approved Chaos Cup. This task
produces measurement only: it does not rebalance the game, does not auto-tune
weights or factors, and introduces no quotas or format changes.

Fixed inputs under test (unchanged by this task):

- Outer selection weights: 35% effective bonus / 30% performance /
  25% league-adjusted form / 5% Chaos form / 10% career prestige.
- Chaos career weight: 450 (`MSS-104-v1`; champion 675, Final last place 13,
  best elimination exit 10, exits 10/7/4/2/1 strictly below Final P32).
- Chaos format v1: 1024 entrants (all source-season league athletes plus Pool
  invitations, 128 per sporting color), six 32-athlete group stages
  [32,16,8,4,2,1], top 16 from each nonfinal group, 16 game rounds per group,
  active bonus used but not earned, first draw exactly 4/color/group, later
  draws pure random.

## Harness

- Test harness: `tests/MtgSoloSports.Tests/Features/Diagnostics/ChaosCalibrationHarness.cs`.
- Opt-in test: `ChaosCalibrationTests.OptIn_ChaosCalibration_SimulatesAndAggregates`.
- Fast deterministic suite in normal CI (`ChaosCalibrationTests`): pinned
  weights/format sizes, five-P6 Superleague careers versus the 450-weight
  champion, Pool champion plus later F3 credit, Final P32 versus fifth-stage
  exit, synthetic Chaos-attribution flips, tier mapping, and manually
  interleaved versus bulk-group RNG equivalence.
- Set `MTG_CHAOS_CALIBRATION=1` to simulate; seasons via
  `MTG_CHAOS_CALIBRATION_SEASONS` (1..500, default 5; 50 routine, up to 500
  stress outside CI time limits), seed via `MTG_CHAOS_CALIBRATION_SEED`,
  stream via `MTG_CHAOS_CALIBRATION_STREAM` (defaults 4242/777).
- The harness simulates N seasons through the same `SimulateSeasonsHandler`
  lifecycle as manual play (Chaos runs every eligible season), then aggregates
  the persisted Chaos editions/entrants/finalists plus the persisted
  Color/Type selection reports (decoded, never recomputed). Read-only: no
  sporting state is written and production behavior is untouched when disabled.
- Per-edition report: entrant/finalist/medal counts by source tier
  (Super/F1/F2/F3/Pool) and by sporting color, stage-group sizes, plus the
  career reference (675/1500/1200/600) and the 5% Chaos selection attribution
  (counterfactual with neutral chaos norm 1000: which top-4 picks change).
- Re-aggregation determinism is asserted in-test: aggregating the same save
  twice yields the identical checksum.

## Representative runs (small, reproducible)

Commands (Release, Linux):

```bash
MTG_CHAOS_CALIBRATION=1 MTG_CHAOS_CALIBRATION_SEASONS=1 \
  dotnet test tests/MtgSoloSports.Tests/MtgSoloSports.Tests.csproj \
  --filter "FullyQualifiedName~ChaosCalibrationTests.OptIn_ChaosCalibration_SimulatesAndAggregates" \
  --configuration Release --logger "console;verbosity=detailed"

MTG_CHAOS_CALIBRATION=1 MTG_CHAOS_CALIBRATION_SEASONS=2 \
  dotnet test tests/MtgSoloSports.Tests/MtgSoloSports.Tests.csproj \
  --filter "FullyQualifiedName~ChaosCalibrationTests.OptIn_ChaosCalibration_SimulatesAndAggregates" \
  --configuration Release --logger "console;verbosity=detailed"
```

Results (seed 4242, stream 777, rules v3, chaos format v1):

- 1 season: passed in 33s. 1 Chaos edition, checksum
  `fa51d553b8017ca424e775ecd4429021cb13ec3b1d62aa9ceb47609448e31bd3`
  (same-save re-aggregation reproduces it exactly).
- 2 seasons: passed in 1m08s. 2 Chaos editions, checksum
  `cdeb580aaaa55840ba218f30951814dd145e37421aa05d84071cd5a83425ca7c`
  (same-save re-aggregation reproduces it exactly; the Season 1 prefix matches
  the 1-season run edition exactly).

### Entrants, finalists and medals

Season 1 has no Superleague yet (ordinary 768 actives + 256 Pool); Season 2 has
the full pyramid (800 actives + 224 Pool). Every edition holds exactly 1024
entrants, 128 per sporting color, stage sizes [32,16,8,4,2,1], 32 finalists
and 3 medals.

| Edition | Super | F1 | F2 | F3 | Pool |
| --- | --- | --- | --- | --- | --- |
| S1 entrants | 0 | 256 | 256 | 256 | 256 |
| S1 finalists | 0 | 12 | 9 | 8 | 3 |
| S1 medals | 0 | 1 | 2 | 0 | 0 |
| S2 entrants | 32 | 256 | 256 | 256 | 224 |
| S2 finalists | 5 | 17 | 7 | 1 | 2 |
| S2 medals | 1 | 1 | 1 | 0 | 0 |

Per-color finalists spread without color seeding beyond the first draw
(S1 finalists 0..7 per color with 128 entrants each; S2 finalists 1..6 per
color with 128 entrants each; medals spread 0..1 per color across both
editions). Pool invitations reach the Final (3/256 in S1, 2/224 in S2) but
take no medals in these two editions; every color keeps its exact 128-entrant
quota.

### Champion versus Super/Color/F1 careers

Reference scores from `CareerPerformanceScorer` (same function as Hall of
Fame and Reports):

- Chaos champion (P1/32, weight 450): 675.
- Chaos Final last place (P32/32): 13, strictly above the best exit (10).
- Chaos exits: 10/7/4/2/1 for the 2/4/8/16/32-group stages.
- Superleague champion: 1500. Color Cup individual champion: 1200.
  Feeder 1 champion: 600.
- Five Superleague P6 finishes: 5 x 843 = 4215, comfortably above one Chaos
  title (675), so the 450 weight rewards a Chaos run without trapping elite
  league careers beneath it.

A Pool invite that wins Chaos keeps the 675-point champion credit and a later
F3 placement adds its own category points (pinned in-test: Chaos 675 + F3
title, total is the exact sum, MajorWins 1, LeagueTitles 1).

### Attributable effects of the 5% Chaos form

Counterfactual per team ranking: final with neutral chaos norm 1000 versus
the persisted final. A changed top-4 set counts as chaos-flipped (local
attribution; Type Cup global allocation can add a second layer).

- S1 (Color Cup only): 2 of 8 color teams flipped by Chaos
  (White gap +8 with chaosDelta +253; Multicolor gap +7 with chaosDelta +129).
- S2 adds the Type Cup: 2 of 5 type teams flipped by Chaos in this window
  (Elf gap -310 with chaosDelta -246; Wizard gap -69 with chaosDelta +493).
  Negative gaps are the documented global-allocation tradeoff (a higher-rated
  athlete plays for another creature-type team), not a rating inversion; the
  harness keeps such cases inspectable.
- The 5% swing is at most 50 rating points (1000 x 50 / 1000), so it decides
  close calls only: pinned in-test, a close call flips while a large bonus gap
  holds.

## What the runs say about fairness (no tuning)

- Stage sizes, field size, per-color quotas and advancement counts validate
  exactly in every edition; no silent format drift.
- Finalists come from every tier including Pool; medals in these two editions
  come from Super/F1/F2 with F3 and Pool shut out, which matches the small
  window (F3 1 finalist, Pool 5 finalists across 2 editions) rather than a
  structural block: the exit/final credit curve and the selection-rating
  regression suites prove the F3/Pool paths stay structurally open.
- Per-color representation stays balanced (exact 128 entrants, spread
  finalists) with no color shut out across the aggregate (3..12 finalists per
  color over 2 editions).
- The 450-weight champion sits below Superleague/Color titles and just above
  an F1 title (675 vs 1500/1200/600), and five mid-table Superleague seasons
  dominate it (4215 vs 675): the intended hierarchy emerging from weights,
  not from a quota.

## Known limitations of the short window

- One and two seasons cannot settle long-run F3/Pool breakthrough rates or
  prestige accumulation over dozens of seasons. The harness supports up to 500
  seasons via `MTG_CHAOS_CALIBRATION_SEASONS`; a 50-season run is the routine
  calibration window and stays opt-in outside normal CI (expect of order
  one hour on this runner from the 1-season 33s baseline plus per-season
  finalization growth; run it when the calibration question needs it).
- The test catalog carries 260 athletes per color with five creature types
  across actives, so the Type Cup fields five teams here; real catalogs with
  hundreds of types will produce larger, more varied fields.
- Type Cup attribution is local per-type top-4 comparison; the global
  maximum-teams allocation can flip membership beyond the local ranking (the
  negative-gap examples above). A future refinement could split Type
  near-misses by "plays elsewhere" vs "not placed".
- The calibration is diagnostics-only: it records weights/factors/format per
  run and never adjusts them.

## Suggested changes (separate, not applied)

None proposed now. Candidate follow-up triggers (not automatic retuning):

- Review strength or Chaos-form weight only if F3 stays medal-less through a
  50-season window with the same structural-openness regression coverage
  still passing; prefer evidence from the 50-season harness over the
  2-season window above.
- Review prestige weight only if Superleague selection share approaches
  totality as titles accumulate over the long window.
- Consider shortlist-tier distribution aggregation if future tuning needs
  full-field rating distributions rather than shortlist-plus-denominators.

## Verification performed

1. Normal backend path plus the new fast `ChaosCalibrationTests` suite
   (weights/format pins, 5xP6, Pool+F3, P32-vs-exit, attribution flips,
   tier mapping) in normal CI.
2. Opt-in calibration: 1 season (checksum
   `fa51d553b8017ca424e775ecd4429021cb13ec3b1d62aa9ceb47609448e31bd3`)
   and 2 seasons (checksum
   `cdeb580aaaa55840ba218f30951814dd145e37421aa05d84071cd5a83425ca7c`);
   the 2-season run reproduces the 1-season Season 1 edition exactly.
3. Same-save re-aggregation reproduces each checksum exactly (asserted
   in-test for the opt-in runs).
4. Manually interleaved (round-by-round across two groups) versus bulk-group
   completion yields byte-identical payloads and standings (asserted in-test
   from a forked Season 1 field).
