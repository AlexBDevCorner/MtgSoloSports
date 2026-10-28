# Reports (MSS-086)

Human-readable exports for ChatGPT sourcing. Snapshots of persisted data, never AI commentary and never DOM scraping.

## Endpoints

- `GET /api/saves/{saveId}/athletes/{athleteId}/report?format=json|md`
- `GET /api/saves/{saveId}/leagues/{leagueId}/seasons/{seasonNumber}/report?format=json|md`

`format` defaults to `json`; `md` and `markdown` select Markdown. Anything else is `400`.
Missing save is `404`; missing athlete or season/league is `404`; corrupt sporting state is `400`.
Downloads are read-only: one `AsNoTracking` context in one committed read transaction
for a consistent snapshot across concurrent simulation commits, no `SaveChanges`, no RNG mutation. Provisional
standings reuse the live-table kernel with a discarded RNG fork, exactly like `CurrentStandings`.
Corrupt stage cursors abort via `GlobalStageGate` and surface as `400`; reports never
substitute a synthetic stage value.

## Contract (v1)

JSON envelope: `schemaVersion: 1`, `reportType` (`athlete-profile` | `standings-table`), `saveId`,
`generatedUtc`, `save { name, currentSeason, phase, schemaVersion, rulesVersion }`,
scope ids, `snapshot` phase/season/stage info, typed `data`, plus a `units` block.
Markdown carries the same scope/status/metadata with headings and tables; it never embeds JSON.

## Fields and units

- All sporting values are fixed-point thousandths integers in JSON.
  Display = value / 1000 (points `77000 = 77.000`; bonus `7552 = +7.552%`).
- `null`/— means unavailable or not-applicable; `0` is an exact zero.
- League totals are official season championship points (32-position table, no bonus multiplier).
  Weighted Career Leaders points are never substituted.
- Athlete report: identity, career, season-by-season league/tier/results, honours, movements,
  Cup selections/history, record holdings. Empty sections are explicit.
- Standings report: exactly the requested season + league, rank order, all 32 rows when present,
  `status` (`final` | `provisional`), `isFinal`, `completedStages/totalStages/globalStage`,
  `seasonChecksum`, league identity/tier/colour, per-row zone only when final.
- Zones (final only): Superleague `safe/qualifier/relegation`; Feeder 1
  `champion/qualifier-up/safe/qualifier-hold/relegated`; Feeder 2
  `promoted/qualifier-up/qualifier-hold/relegated`; Feeder 3
  `promoted/qualifier-up/safe`. Provisional rows omit zones.

## Files

- `athlete-{id}-{slug}.md|json`, `standings-season-{season}-league-{leagueId}-{slug}.md|json`
  (ASCII slug; full UTF-8 names stay in the body).
- `Content-Type: text/markdown; charset=utf-8` or `application/json; charset=utf-8`,
  `Content-Disposition: attachment`.
