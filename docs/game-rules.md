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

`base points × (1 + active bonus)`

Fractional results are retained exactly by the implementation using fixed-point integers.

## 7. Stage and season standings

A stage score is the sum of final round points from its 16 rounds. At stage completion, athletes receive championship points using the same 32-position scoring table, with no bonus multiplier applied to those championship points.

The season champion is the athlete with the greatest total championship points across 32 stages. Raw stage/round data is retained for history and tie-breaking.

Tie-breaking is deterministic: stage-place counts from best downward, then round-place counts, then raw totals, then a seeded random draw only if everything remains equal.

## 8. Career bonus

Bonus affects future point value, never shuffle probability.

Regular-league round bonus for places 1-10:

`+0.10, +0.09, +0.08, +0.07, +0.06, +0.05, +0.04, +0.03, +0.02, +0.01`

Regular-league stage bonus for places 1-10:

`+0.20, +0.18, +0.16, +0.14, +0.12, +0.10, +0.08, +0.06, +0.04, +0.02`

Superleague awards double these bonus amounts.

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

Season 1 has only the eight feeder leagues. Each league randomly draws 32 of its 256 save athletes.

After Season 1, positions 1-4 from each feeder league enter the inaugural Superleague: 8 × 4 = 32. They leave their feeder leagues and retain all bonus history. Vacancies are filled from the common pool after movement is resolved.

## 10. Superleague movement from Season 2

At season end:

- Superleague positions 1-16 are safe.
- positions 25-32 (8 athletes) are automatically relegated;
- positions 17-24 (8 athletes) enter the Superleague Qualifier;
- every feeder-league champion (8 athletes) is automatically promoted;
- feeder-league positions 2-4 (24 athletes) enter the qualifier.

The next Superleague therefore contains:

`16 safe incumbents + 8 feeder champions + 8 qualifier winners = 32`.

There are no color quotas in Superleague.

## 11. Superleague Qualifier

The qualifier contains 32 athletes: 8 Superleague incumbents plus 24 feeder challengers. It behaves like one standard 16-round stage. Active bonus applies; no new bonus is generated. The top 8 qualify/remain in Superleague. Results are career history but not normal league championship points.

## 12. Feeder rebalancing and common pool

All Superleague movement/qualifier outcomes are resolved first.

For each feeder league:

1. remove athletes entering Superleague;
2. add returning athletes of that sporting color;
3. if above 32, send the lowest-ranked remaining athletes from the previous feeder season to the common pool;
4. if below 32, randomly select eligible athletes from that color's common pool until 32.

Pool selection is equal-probability. Former league athletes can return immediately; no cooldown exists.

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

### Individual event

All 32 selected athletes compete in one standard 16-round stage. The event awards Gold/Silver/Bronze and an official individual Color Cup title. No bonus is earned.

### Team event

Four groups exist: all team #1 athletes compete against each other, then all #2, #3 and #4 athletes. Each group has **8 rounds**. Team score is the sum of the four athletes' scores. Active bonus applies; no bonus is earned. Team medals/titles and individual leg statistics are recorded.

## 15. Type Cup

Type Cup is team-only. Every creature type capable of fielding four distinct currently active athletes may participate; there is no artificial team limit.

An athlete with multiple types is initially uncapped. During selection it prefers the type where it ranks higher (for example #1 Wizard over #4 Human). A deterministic global allocation resolves conflicts and should maximize valid four-athlete teams while respecting permanent nationality.

Once an athlete actually appears in a Type Cup for a type, that becomes its permanent Type Cup nationality; it can never represent another type later.

Competition uses the same #1/#2/#3/#4 team-group model as the Color Cup team event, with 8 rounds per group. Active bonus applies; no bonus is generated.

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

Official major honours:

- eight feeder-league championships;
- Superleague championship;
- Color Cup individual championship;
- Color Cup team championship;
- Type Cup team championship.

Stages are important statistics, not major trophies. Qualifier success is a career event, not a major title.

The UI supports both instant simulation and an optional Eurovision-like card-by-card reveal. The engine always calculates and persists the full round first; presentation only replays immutable facts.

## 18. Determinism

Every save stores a random algorithm/version and RNG state. Universe selection, league draws, round shuffles, pool draws and unavoidable random tie-breaks consume only this controlled RNG. Equivalent starting state + rules + RNG state must produce equivalent results.
