# Cup Selection Calibration (MSS-066)

Validation and observability for the tier-aware Cup selection rating.
This task produces evidence for future tuning: it does not rebalance the game,
does not auto-tune weights or factors, and introduces no tier quotas or
absolute league-priority rule.

Fixed inputs under test (unchanged by this task):

- Outer weights: 35% effective bonus / 30% league-adjusted performance /
  25% league-adjusted form / 10% league-aware prestige.
- Performance/form strength factors: Super/F1/F2/F3 = 1000/800/600/400.
- Bonus generation tiers: Super/F1/F2/F3 = 2/1/1/2/1/4 (unchanged).
- Prestige: tiered v3 quarter-point model (unchanged).

## Harness

- Test harness: `tests/MtgSoloSports.Tests/Features/Diagnostics/CupSelectionCalibrationHarness.cs`.
- Opt-in test: `CupSelectionCalibrationTests.OptIn_CupCalibration_SimulatesAndAggregates`.
- Fast deterministic regression suite (normal CI):
  `tests/MtgSoloSports.Tests/SimulationKernel/Cups/CupSelectionCrossTierRegressionTests.cs`
  (10 tests: identical-input hierarchy, F3-not-equivalent-to-F1, legitimate
  lower-tier upsets without quota, former-superstar bonus/prestige
  competitiveness and decay, pool with stale history scores zero, Type Cup
  hierarchy and upset paths, pinned weights/factors).
- Normal CI also runs fast synthetic aggregation tests in
  `CupSelectionCalibrationTests` (tier mapping, cross-tier detection,
  checksum determinism, fixed weights) without simulating.
- Set `MTG_CUP_CALIBRATION=1` to simulate; seasons via
  `MTG_CUP_CALIBRATION_SEASONS` (1..500), seed via `MTG_CUP_CALIBRATION_SEED`,
  stream via `MTG_CUP_CALIBRATION_STREAM`.
- The harness simulates N seasons through the same `SimulateSeasonsHandler`
  lifecycle as manual play, then aggregates the persisted `CupSelectionReports`
  (decoded, never recomputed) by source-season league level, plus full-field
  candidate denominators from season memberships. Read-only: no sporting
  state is written and production behavior is untouched when disabled.
- Per-tier statistics: candidate count, selected count and rate, average and
  median final rating of selected athletes, average normalized
  bonus/performance/form/prestige of selected athletes, selected-vs-nearest-miss
  component breakdowns, and inspectable lower-tier-ahead-of-higher-tier
  examples with the dominant component behind each upset.
- Re-aggregation determinism is asserted in-test: aggregating the same save
  twice yields the identical checksum.

## Representative run (primary)

Command (Release, Linux):

```bash
MTG_CUP_CALIBRATION=1 MTG_CUP_CALIBRATION_SEASONS=20 \
  dotnet test tests/MtgSoloSports.Tests/MtgSoloSports.Tests.csproj \
  --filter "FullyQualifiedName~CupSelectionCalibrationTests.OptIn_CupCalibration_SimulatesAndAggregates" \
  --configuration Release --logger "console;verbosity=detailed"
```

Result: passed in 48m11s.

- Run seed 4242, stream 777, 20 seasons, rules v3.
- Weights recorded: bonus=350 performance=300 form=250 prestige=100.
- Factors recorded: super=1000 f1=800 f2=600 f3=400.
- Cup editions observed: 20 (Season 1/3/5/7/9/11/13/15/17/19 Color Cup,
  Season 2/4/6/8/10/12/14/16/18/20 Type Cup).
- Aggregate checksum: `81f00b878eab3e6fecb7b1ef91a1396bd10c2ba48dc9f1fc8f28407f03417e13`
  (re-aggregation of the same save reproduces it exactly).
- The first six editions reproduce the earlier 6-season run exactly
  (same per-edition selection counts and the same S2 Elf / S3 Green / S5
  Green examples), confirming deterministic simulation for the shared prefix.

### Selection distribution by source tier (primary run)

Color Cup editions select 32 athletes (8 colors x top four).
Type Cup editions allocate 20 athletes (5 creature-type teams x four).

| Edition | Super sel. | F1 sel. | F2 sel. | F3 sel. | Pool sel. |
| --- | --- | --- | --- | --- | --- |
| S1 Color | 0/0 (no Superleague yet) | 32/256 (12.5%) | 0/256 | 0/256 | 0/1280 |
| S2 Type | 15/32 (46.9%) | 5/256 (2.0%) | 0/256 | 0/256 | n/a (active only) |
| S3 Color | 19/32 (59.4%) | 13/256 (5.1%) | 0/256 | 0/256 | 0/1248 |
| S4 Type | 11/32 (34.4%) | 9/256 (3.5%) | 0/256 | 0/256 | n/a |
| S5 Color | 19/32 (59.4%) | 12/256 (4.7%) | 1/256 (0.4%) | 0/256 | 0/1248 |
| S6 Type | 15/32 (46.9%) | 5/256 (2.0%) | 0/256 | 0/256 | n/a |
| S7 Color | 18/32 (56.2%) | 14/256 (5.5%) | 0/256 | 0/256 | 0/1248 |
| S8 Type | 11/32 (34.4%) | 9/256 (3.5%) | 0/256 | 0/256 | n/a |
| S9 Color | 21/32 (65.6%) | 11/256 (4.3%) | 0/256 | 0/256 | 0/1248 |
| S10 Type | 13/32 (40.6%) | 7/256 (2.7%) | 0/256 | 0/256 | n/a |
| S11 Color | 19/32 (59.4%) | 13/256 (5.1%) | 0/256 | 0/256 | 0/1248 |
| S12 Type | 14/32 (43.8%) | 6/256 (2.3%) | 0/256 | 0/256 | n/a |
| S13 Color | 22/32 (68.8%) | 10/256 (3.9%) | 0/256 | 0/256 | 0/1248 |
| S14 Type | 16/32 (50.0%) | 4/256 (1.6%) | 0/256 | 0/256 | n/a |
| S15 Color | 20/32 (62.5%) | 12/256 (4.7%) | 0/256 | 0/256 | 0/1248 |
| S16 Type | 15/32 (46.9%) | 5/256 (2.0%) | 0/256 | 0/256 | n/a |
| S17 Color | 21/32 (65.6%) | 11/256 (4.3%) | 0/256 | 0/256 | 0/1248 |
| S18 Type | 14/32 (43.8%) | 6/256 (2.3%) | 0/256 | 0/256 | n/a |
| S19 Color | 18/32 (56.2%) | 14/256 (5.5%) | 0/256 | 0/256 | 0/1248 |
| S20 Type | 16/32 (50.0%) | 4/256 (1.6%) | 0/256 | 0/256 | n/a |
| Aggregate | 317/608 (52.1%) | 202/5120 (3.9%) | 1/5120 (0.0%) | 0/5120 | 0/12512 |

Average final rating of selected athletes stays ordered Super > F1 wherever
both tiers are selected (e.g. S5 Color: Super 855, F1 798, F2 752;
S11 Color: Super 850, F1 807; S13 Color: Super 830, F1 721;
S2 Type: Super 754, F1 637; S16 Type: Super 731, F1 642),
which is the intended hierarchy emerging from ratings, not from a quota.
The single F2 selection (S5 Color, avg 752) sits below that edition's Super
and F1 averages, as expected for a rare upset rather than a tier inversion.

### Notable cross-tier selections (primary run)

Every post-Season-1 edition contains lower-tier athletes selected ahead of
higher-tier candidates on rating, with the component explanation recorded:

- S5 Color Green: `Green Athlete 0190` (F2, final 752, dominant **form**)
  ahead of `Green Athlete 0046` (Super, 650), gap +102, and ahead of
  `Green Athlete 0076` (Super, 691), gap +61. The only F2 selection in the
  20-season run: an exceptional form season overcoming the 0.6-vs-1.0 factor gap.
- S16 Type Human: `Red Athlete 0066` (F1, final 870, **performance**) ahead of
  `Blue Athlete 0129` (Super, 480), gap +390; the same F1 athlete also beats
  three other Super athletes by +377 to +380 on performance/form.
- S11 Color Red: `Red Athlete 0003` (F1, final 913, **performance**) ahead of
  `Red Athlete 0121` (Super, 539), gap +374.
- S7 Color Colorless: `Colorless Athlete 0170` (F1, final 880, **performance**)
  ahead of `Colorless Athlete 0130` (Super, 508), gap +372.
- S9 Color Red: `Red Athlete 0016` (F1, final 883, **form**) ahead of
  `Red Athlete 0029` (Super, 527), gap +356.
- S3 Color Green: `Green Athlete 0102` (F1, final 1000, **form**) ahead of
  `Green Athlete 0086` (Super, 687), gap +313.

Cross-tier counts per edition: S1 0, S2 2, S3 14, S4 16, S5 25, S6 7,
S7 20, S8 15, S9 13, S10 13, S11 11, S12 11, S13 10, S14 6, S15 10,
S16 13, S17 9, S18 12, S19 17, S20 3.

### Nearest-miss behavior (primary run)

130 selected-vs-nearest-miss comparisons were collected (one per Color team
and per fielded Type team). Color cuts are usually tight same-tier contests
(e.g. S1 Blue: F1 775 vs F1 774, gap +1; S1 Green: F1 868 vs F1 809,
gap +59). One Type Cup comparison is negative
(S2 Elf: weakest member F1 624 vs best non-member Super 768, gap -144):
that higher-rated athlete plays for another creature-type team, which is the
documented global-allocation tradeoff (maximum teams first, strongest squads
second), not a rating inversion. The harness keeps such cases inspectable;
a future refinement could split Type near-misses by "plays elsewhere" vs
"not placed".

## Second seed (robustness)

```bash
MTG_CUP_CALIBRATION=1 MTG_CUP_CALIBRATION_SEASONS=4 \
MTG_CUP_CALIBRATION_SEED=987654 MTG_CUP_CALIBRATION_STREAM=321 \
  dotnet test ... # same filter as above
```

Result: passed in 2m50s. Same structural conclusions with a different seed:

- S1 Color again sweeps F1 (32/256, others 0): with no Superleague and no
  career history, the 0.8 factor beats 0.6/0.4 over equivalent distributions
  and pool scores zero. The sweep is the hierarchy working, not a quota.
- Super selection rates 50-62%, F1 2-5%, F2/F3/Pool 0 across S2-S4.
- Cross-tier F1-over-Super upsets in every post-S1 edition (S2 7, S3 12,
  S4 11), all form/performance-driven (e.g. S3 Green F1 997 vs Super 675,
  gap +322, dominant form).
- Aggregate: Super 52/96 (54.2%, avg final 783), F1 52/1024 (5.1%, avg 799),
  F2/F3/Pool 0. Note F1's aggregate average final slightly exceeds Super's
  here because Type Cup global normalization compresses Super ratings while
  Color Cup color-local normalization spreads them; per-edition medians keep
  the intended order. No conclusion depends on the seed.

## Component dominance

No component dominates unexpectedly across the 20-season run:

- Bonus (35%) and prestige (10%) tilt toward Superleague athletes as
  designed: selected Super athletes average ~820-955 bonus norm across
  editions (typically 900+ in Color editions) and prestige norms that climb
  with history (Color editions ~700-830 by S13-S19 vs ~350 in S2; Type
  editions ~350-600), compared with ~580-720 bonus and ~200-660 prestige
  for selected F1 athletes, reflecting tiered bonus generation and tiered
  prestige.
- Performance (30%) and form (25%) decide the upsets: every recorded
  cross-tier example is form- or performance-dominant, and selected F1
  athletes match or beat Super athletes on those norms
  (e.g. S5 Color: F1 perf 947 vs Super 849; S19 Color: F1 perf 955 vs
  Super 819).
- Prestige never single-handedly carries a selection in the observed
  examples: with only 10% weight, even the maximum prestige norm (+100)
  cannot cover a large adjusted performance/form gap, which matches the
  decay scenario in the regression suite (faded bonus + prestige alone
  loses to a clearly stronger current-season tier).

## What the run says about F3 and Pool

- F3 recorded zero selections in 20 Cup editions (5120 candidate-slots) in the
  primary run, and zero in 24 editions across both seeds. An F3
  athlete needs roughly double the unadjusted output to match an equivalent
  F1 athlete's adjusted rating (0.4 vs 0.8), on top of weaker bonus
  generation (1/4) and slower prestige accrual. The deterministic regression
  suite proves the upset path is structurally open (an exceptional F3 season
  outranks a weak Super/F1 season on final rating with no quota blocking
  it), so the observed zero reflects rarity in a 20-season window, not
  impossibility. The 20-season window strengthens the earlier 6-season
  rarity read (still only one F2 selection in 5120 F2 slots): prestige has
  now accumulated through 20 seasons and F3 still has not broken through,
  but the documented trigger remains F3 at zero through 50 seasons before
  reviewing the 1000/800/600/400 factors, not adding a quota.
- Pool recorded zero selections in 12512 Color candidate-slots in the primary
  20-season run (zero across both seeds). Pool athletes score zero performance and form by design, so even
  maximum bonus plus prestige (450/1000 rating points) cannot beat active
  athletes with ordinary seasons. Long-term pool athletes retain no stale
  recent-form advantage: historical stages never leak into selection inputs
  (proven at both metrics and selection level in the regression suite).

## Known limitations of the synthetic catalog/run

- The test catalog carries 260 athletes per color with only five distinct
  creature types across actives (Human/Wizard, Elf/Druid, Goblin), so the
  Type Cup fields all five viable types (5 teams x 4 = 20 athletes) every
  even season. Real catalogs with hundreds of types will produce larger,
  more varied fields; cross-tier dynamics there may differ.
- Twenty seasons now covers meaningful prestige accumulation (Color Super
  prestige norms ~700-830 by the mid-teens seasons) and a large enough window
  to see that F2 breakthroughs are rare (1 in 5120 F2 slots) and F3
  breakthroughs rarer still (0 in 5120). The harness supports up to 500
  seasons via `MTG_CUP_CALIBRATION_SEASONS`; a 20-season run takes ~48 minutes
  on this runner (Release, Linux) and longer windows remain opt-in outside
  normal CI.
- Aggregation covers the persisted shortlists (top 12 per color/type) plus
  full-field candidate denominators from memberships; full-field rating
  distributions are not stored and were not reconstructed.
- Type Cup near-miss comparisons do not yet split "plays for another team"
  from "not placed"; the single negative-gap example above is the former.
- The calibration is diagnostics-only: it records weights/factors per run
  and never adjusts them. Candidate follow-up tuning (not this task): review
  strength factors if F3 stays at zero through 50 seasons; review prestige
  weight if Superleague selection share approaches totality as titles
  accumulate; consider shortlist-tier distribution aggregation.

## Verification performed

1. Normal backend suite plus the 10-test synthetic cross-tier regression
   suite (in normal CI).
2. Opt-in calibration: 20 seasons (seed 4242, checksum
   `81f00b878eab3e6fecb7b1ef91a1396bd10c2ba48dc9f1fc8f28407f03417e13`)
   + 4 seasons (seed 987654); both seeds show the same structural pattern
   (Super ~50-55%, F1 ~2-5%, F2/F3/Pool ~0%, regular F1-over-Super upsets on
   form/performance, and the 20-season first-six editions reproduce the
   earlier 6-season prefix exactly).
3. Same-save re-aggregation reproduces the checksum exactly (asserted
   in-test for the 20-season run).
4. Lower-tier-beats-higher-tier examples inspected for Color and Type Cups;
   each carries the dominant-component explanation (form/performance).
5. No F3 selection was treated as equal to an equivalent F1/Superleague
   season: identical unadjusted inputs rank Super > F1 > F2 > F3 by final
   rating (regression suite), and the long-run averages preserve that order.
6. Player selection-report UI still renders: backend report round-trip tests
   (v1/v2/v3 payloads) pass, frontend typecheck/build pass, and all 426
   frontend tests pass, including the new tier-explanation tests. The ranking
   table adds a League column (tier + factor hover text); legacy reports
   without tier data render exactly as before.
