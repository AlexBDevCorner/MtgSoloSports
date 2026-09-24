# Implementation Roadmap

The authoritative executable task specifications live in the `AutonomousWork` repository under `projects/mtgsolosports/tasks/`.

The planned order is intentionally feature-first:

1. Repository quality/test foundation
2. Deterministic rules/RNG/scoring kernel
3. Save persistence
4. MTG creature catalog and sporting-color classification
5. 2,048-athlete universe creation
6. Season 1 league initialization/inaugural draw
7. Round simulation
8. Stage simulation and bonus activation
9. Global stage/season standings
10. Initial live round and standings UI
11. Athlete career profile/history
12. Inaugural Superleague
13. Automatic movement
14. Superleague Qualifier
15. Feeder rebalancing/common pool
16. Full season lifecycle / Next Event
17. Fast/multi-season simulation equivalence
18. Exact historical round replay
19. Story events and records/Hall of Fame
20. Color Cup selection + individual + team
21. Type Cup allocation + team competition
22. Eurovision-style animated reveal
23. Save export/import/recovery
24. Long-run performance and 1000-season hardening

Tasks are split smaller than these headings in the control repo so coding-agent PRs stay local and reviewable.
