# Game Rules Specification v1

## 1. Concept

MtgSoloSports is a single-player, self-running sports simulation using Magic: The Gathering creature cards as athletes. The player observes a persistent universe rather than controlling a team. Randomness determines a round's base finishing order; accumulated historical bonus changes the value of that position, not the probability of receiving it.

## 2. Athletes and sporting colors

Only MTG **Creature** cards participate. Tokens and all non-creature cards are excluded.

One unique card name is one athlete. Different printings of the same card name are the same athlete; different card names representing the same lore character are separate athletes.

MTG gameplay strength, power/toughness, rarity, mana value, abilities and legendary status have no sporting effect.

There are eight sporting colors based on **printed card color**, not color identity:

1. White
2. Blue
3. Black
4. Red
5. Green
6. Multicolor
7. Hybrid
8. Colorless

Rules:

- mono-color -> matching league;
- two or more colors -> Multicolor;
- hybrid mana -> Hybrid (Hybrid takes precedence over Multicolor);
- Devoid -> Colorless;
- colorless artifact creature -> Colorless;
- colored artifact creature -> printed color;
- double-faced/transform cards use the front face.

## 3. Save universe

A new save contains exactly **2,048 athletes**, equally distributed: **256 per sporting color**. The selection is random from the available creature catalog while satisfying those quotas. Cards outside the selected 2,048 do not exist in that save in v1.

## 4. Leagues

Eight regular leagues correspond to the eight sporting colors. Each contains **32 athletes**.

Starting in Season 2, Superleague also contains **32 athletes**. An athlete belongs to only one active league per season. All other save athletes belong to the common pool.

## 5. Season structure

Each league season has **32 stages**. Each stage has **16 rounds**. All leagues progress stage-by-stage: Stage N+1 cannot begin until Stage N is complete for every active league, though the UI may simulate the leagues one by one.

## 6. Round simulation

All 32 league athletes are randomly shuffled to create the base finishing order. Historical bonus does not affect the shuffle.

### Scoring table

| Position | Points | Position | Points |
|---:|---:|---:|---:|
| 1 | 77 | 17 | 16 |
| 2 | 67 | 18 | 15 |
| 3 | 58 | 19 | 14 |
| 4 | 50 | 20 | 13 |
| 5 | 43 | 21 | 12 |
| 6 | 37 | 22 | 11 |
| 7 | 32 | 23 | 10 |
| 8 | 28 | 24 | 9 |
| 9 | 25 | 25 | 8 |
| 10 | 23 | 26 | 7 |
| 11 | 22 | 27 | 6 |
| 12 | 21 | 28 | 5 |
| 13 | 20 | 29 | 4 |
| 14 | 19 | 30 | 3 |
| 15 | 18 | 31 | 2 |
| 16 | 17 | 32 | 1 |

Final round points:

`base points × (1 + active bonus / 100)`

Active bonus is a percentage: an athlete on +7.552% who wins a round scores 77 × 1.07552 = 82.815 points. Results are computed with fixed-point integers and truncated to thousandths of a point.

## 7. Stage and season standings

A stage score is the sum of final round points from its 16 rounds. At stage completion, athletes receive championship points using the same 32-position scoring table, with no bonus multiplier applied to those championship points.

The season champion is the athlete with the greatest total championship points across 32 stages. Raw stage/round data is retained for history and tie-breaking.

Tie-breaking is deterministic: stage-place counts from best downward, then round-place counts, then raw totals, then a seeded random draw only if everything remains equal.

## 8. Career bonus

Bonus affects future point value, never shuffle probability.

Regular-league round bonus for places 1-10:

`+0.10%, +0.09%, +0.08%, +0.07%, +0.06%, +0.05%, +0.04%, +0.03%, +0.02%, +0.01%`

Regular-league stage bonus for places 1-10:

`+0.20%, +0.18%, +0.16%, +0.14%, +0.12%, +0.10%, +0.08%, +0.06%, +0.04%, +0.02%`

Superleague awards double these bonus amounts.

### 8a. Tiered feeder model (Rules v2)

New saves use Rules v2 with a four-level pyramid (each league still 32 athletes):

**Superleague → Feeder 1 → Feeder 2 → Feeder 3**

Only newly earned career bonus is scaled; championship points, base round
scoring, stage/season ranking, shuffle probability, tie-breaks, Cup scoring,
and Cup no-new-bonus behavior are unchanged:

- Superleague: **2/1** (double the v1 regular bonus);
- Feeder 1: **1/1** (the v1 regular bonus);
- Feeder 2: **1/2** (half, truncated);
- Feeder 3: **1/4** (quarter, truncated).

Fractional scaling is exact fixed-point integer arithmetic with one rule:
multiply first, then integer-divide, truncating toward zero
(for example a base +0.09% at Feeder 3 earns +0.02%).
Historical v1 snapshots stay valid through the v1 compatibility path and are
never reinterpreted as tiered; historical contributions keep the amount earned
at their original tier and are never rescaled by later movement. A feeder
championship remains a feeder championship for honours/prestige regardless of
division.

Bonus earned during a stage becomes active only from the **next stage**. Stage 32 bonus first becomes usable in the next season.

Bonus contribution weight by season age is linear:

- same season: 100%
- one season old: 80%
- two: 60%
- three: 40%
- four: 20%
- five or more: 0%

Stage 32 bonus starts the following season at 80%. Bonus keeps aging while an athlete is in the common pool. There is no bonus cap.

## 9. Season 1 and inaugural Superleague

Season 1 has 24 feeder leagues with no Superleague: one Feeder 1, one Feeder 2
and one Feeder 3 league per sporting color (8 × 3 × 32 = 768 active, 160 pool
per color, 1,280 pool globally). Each color's 256-athlete save population is
name-sorted and shuffled once with the versioned RNG; shuffled positions 1-32
join F1, 33-64 join F2, 65-96 join F3 and 97-256 stay in the common pool. One
persisted draw order is the source of truth so reveal/replay never resimulates.

After Season 1, positions 1-4 from each **F1** feeder league enter the
inaugural Superleague: 8 × 4 = 32. F2/F3 and pool athletes never skip tiers
directly into the inaugural Superleague. Promoted athletes leave their F1
membership, retain all bonus history, and keep their division for the
inaugural carryover (F1 minus promoted stays F1 at 28 each, F2/F3 stay at 32
each). Vacancies are filled from the common pool after movement is resolved.

Compatibility: historical v1 saves (8 single-tier feeders, 256 active, 1,792
pool) remain openable and their Season/Stage/Round/Standing/Bonus/Cup/Movement
history is never rewritten or reinterpreted as tiered. A v1 save enters the
tiered rules only at a safe season boundary (completed source plus a pending
CupComplete next season with rebalanced 32-per-league F1 rosters and no live
simulation): its existing feeders become F1 and exactly 32 F2 plus 32 F3 per
color are seeded from that color's target-season pool with the versioned RNG
(equal-probability, no bonus weighting, no F1 demotion). Saves not at the
boundary stay on v1 until the boundary is reached rather than partially
upgrading a live season.

## 10. Superleague movement from Season 2

At season end:

- Superleague positions 1-16 are safe.
- positions 25-32 (8 athletes) are automatically relegated to F1 of their sporting color;
- positions 17-24 (8 athletes) enter the Superleague Qualifier as incumbents;
- every F1 champion (rank 1 from each of the eight colors, 8 total) is automatically promoted;
- F1 positions 2-4 from each color (24 total) enter the qualifier as challengers.

The next Superleague therefore contains:

`16 safe incumbents + 8 F1 champions + 8 qualifier winners = 32`.

There are no color quotas in Superleague. Every move is one adjacent tier only; there is no division skipping.

## 11. Superleague Qualifier

The qualifier contains 32 athletes: 8 Superleague incumbents plus 24 F1 challengers. It behaves like one standard 16-round stage. Active bonus applies; no new bonus or championship points are generated. The top 8 qualify/remain in Superleague. Results are career history but not normal league championship points.

## 11a. Feeder movement: F1↔F2 and F2↔F3 (per sporting color)

For each color independently, from the just-completed source season standings:

**F1**

- ranks 17-24: qualifier incumbents (8);
- ranks 25-32: automatically relegated to F2 (8).

**F2**

- ranks 1-8: automatically promoted to F1 (8);
- ranks 9-16: qualifier challengers for F1↔F2 (8);
- ranks 17-24: qualifier incumbents for F2↔F3 (8);
- ranks 25-32: automatically relegated to F3 (8).

**F3**

- ranks 1-8: automatically promoted to F2 (8);
- ranks 9-16: qualifier challengers for F2↔F3 (8);
- ranks 17-32: remain in F3 (no automatic pool relegation).

F2 ranks 1-8 are automatic-up only, 9-16 challenge upward only, 17-24 defend downward only, 25-32 automatic-down only. No F2 athlete appears in both qualifiers. An athlete moves at most one tier per postseason; all bands come from the just-completed standings, never provisional positions. No cross-color movement. Pool is not a competitive tier and never participates in a qualifier.

This permits 8 guaranteed plus up to 8 qualifier promotions into F1 per color (at most 16/32 new F1 athletes), and likewise into F2.

## 11b. Feeder qualifiers (16 athletes each)

One 16-athlete / 16-round qualifier per color per boundary (8 F1↔F2 + 8 F2↔F3 = 16 feeder events, plus 1 Superleague = 17 total per ordinary postseason):

- 8 upper-tier incumbents + 8 lower-tier challengers;
- same 16-round simulation principles, active bonus applies, no new bonus or championship points;
- top 8 occupy/remain in the higher tier, bottom 8 occupy/remain in the lower tier.

Canonical RNG order is Superleague first, then F1↔F2 by sporting-color enum, then F2↔F3 by color enum, so equivalent state consumes RNG identically. Each event persists immutable replay payload/checksum/RNG before/after with boundary + color identity; retry resumes from completed events and never reruns them. The qualifiers phase is complete only when all required events are resolved. Qualifier participation is retained as history without counting as a major honour/title.

## 12. Feeder rebalancing and common pool

All automatic movement and all qualifier outcomes are resolved first.
Rebalancing then repairs structural color imbalance per sporting color
through a deterministic tier cascade F1 → F2 → F3 → Pool, preserving 32
athletes in every F1/F2/F3 league. Sporting performance controls normal
adjacent-tier movement; rebalancing only repairs population/color composition.
Pool is not a league, has no qualifier, and only ever connects directly to F3.

Shortage: if F1 is below 32, pull the best eligible retained F2 athlete(s)
upward to F1; the resulting F2 vacancies pull the best eligible retained F3
athlete(s) upward to F2; only the resulting F3 vacancies draw
equal-probability from the same-color common pool (versioned RNG, canonical
color order). Overflow: if F1 is above 32, push the worst eligible retained
F1 athlete(s) down to F2; cascade any F2 overflow to F3 the same way; only F3
overflow displaces to the common pool.

Athletes who just earned adjacent-tier movement (automatic, qualifier outcome,
inaugural creation, Superleague return) are protected and used only when no
retained alternative exists; corrupt rosters fail loudly. Upward refill uses
the best source-season ranks first, downward overflow the worst, with name/id
tie breaks. Only pool draws consume RNG. There is no Pool→F1/F2 or F1/F2→Pool
movement; no normal F3 relegation to pool; no cooldown; bonus ages in pool.

Structural cascade moves persist as RebalanceUp (F2→F1/F3→F2) and
RebalanceDown (F1→F2/F2→F3), distinct from competitive promotion/relegation
and from pool RebalanceDraw (Pool→F3) / RebalanceDisplacement (F3→Pool).

## 13. Post-season cups

One Cup occurs after every season:

- odd seasons: **Color Cup**;
- even seasons: **Type Cup**.

Cups use active bonus but award no championship points and generate no new bonus.

## 14. Color Cup

All eight sporting colors participate with four selected athletes each.

Automatic selection rating:

- 35% current effective bonus;
- 30% completed-season performance;
- 25% recent form over the most recent 10 league stages, weighted toward recent stages;
- 10% career prestige.

Each component is normalized within the color. The four highest scores make the team and are ordered #1-#4 by selection rating.

Career prestige (Rules v1 initial constants) is raw points: feeder-league title 100, Superleague title 300, Superleague season appearance 20, stage win 10, stage second 5, stage third 2, other major honour (future Cup titles) 150. Recent form weights the latest ten league stages 1..10 oldest-to-newest (newest-aligned; missing stages score zero).

### Individual event

All 32 selected athletes compete in one standard 16-round stage. The event awards Gold/Silver/Bronze and an official individual Color Cup title. No bonus is earned.

### Team event

Four groups exist: all team #1 athletes compete against each other, then all #2, #3 and #4 athletes. Each group has **8 rounds**. Team score is the sum of the four athletes' scores. Active bonus applies; no bonus is earned. Team medals/titles and individual leg statistics are recorded.

## 15. Type Cup

Type Cup is team-only. Every creature type capable of fielding four distinct currently active athletes may participate; there is no artificial team limit.

An athlete with multiple types is initially uncapped. During selection it prefers the type where it ranks higher (for example #1 Wizard over #4 Human). A deterministic global allocation resolves conflicts and should maximize valid four-athlete teams while respecting permanent nationality.

Once an athlete actually appears in a Type Cup for a type, that becomes its permanent Type Cup nationality; it can never represent another type later.

Competition uses the same #1/#2/#3/#4 team-group model as the Color Cup team event, with 8 rounds per group. Active bonus applies; no bonus is generated.

Type Cup scoring uses the §6 32-position table exactly for positions 1–32. Positions beyond 32 (possible because there is no artificial team limit) score the table minimum of 1 point before the active-bonus multiplier, applied with the same fixed-point arithmetic. Fields of 2–32 teams are unaffected.

## 16. Season lifecycle

1. Stages 1-32
2. Final league standings
3. Automatic Superleague movement
4. Superleague Qualifier
5. Next Superleague roster
6. Feeder rebalancing/common-pool draws
7. Color Cup or Type Cup
8. Season finalization
9. Bonus aging/decay
10. Next season

Season 1 includes the special inaugural-Superleague creation.

## 17. History and presentation

The application retains permanent career and historical data including league positions, stage/round wins, titles, Cup medals, promotion/relegation, Superleague tenure, bonus history, selections and Type Cup nationality.

Official major honours (MSS-047: each podium finish counts as one honour):

- eight feeder-league championships plus eight runner-up and eight third-place honours per season;
- Superleague championship plus runner-up and third-place honours per season from Season 2;
- Color Cup individual championship plus runner-up and third-place honours;
- Color Cup team championship plus runner-up and third-place honours (four members each);
- Type Cup team championship plus runner-up and third-place honours (four members each).

A win/title/championship remains a 1st-place result only; an honour is any top-three
finish. Stages are important statistics, not major trophies. Qualifier success is a career event, not a major title.

The UI supports both instant simulation and an optional Eurovision-like card-by-card reveal. The engine always calculates and persists the full round first; presentation only replays immutable facts.

## 18. Determinism

Every save stores a random algorithm/version and RNG state. Universe selection, league draws, round shuffles, pool draws and unavoidable random tie-breaks consume only this controlled RNG. Equivalent starting state + rules + RNG state must produce equivalent results.
