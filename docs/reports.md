# Reports (MSS-086/MSS-087/MSS-088)

Human-readable exports for ChatGPT sourcing. Snapshots of persisted data, never AI commentary and never DOM scraping.

## Endpoints

- `GET /api/saves/{saveId}/athletes/{athleteId}/report?format=json|md`
- `GET /api/saves/{saveId}/leagues/{leagueId}/seasons/{seasonNumber}/report?format=json|md`
- `GET /api/saves/{saveId}/cups/{cup}/editions/{seasonNumber}/report?format=json|md` (`cup` = `color` | `type`)
- `GET /api/saves/{saveId}/cups/{cup}/teams/{teamKey}/report?format=json|md`
- `GET /api/saves/{saveId}/leagues/{leagueKey}/report?format=json|md`
- `GET /api/saves/{saveId}/reports/season/{seasonNumber}?format=json|md` — season dossier (MSS-088)
- `GET /api/saves/{saveId}/reports/almanac?format=json|md` — universe almanac (MSS-088)

`format` defaults to `json`; `md` and `markdown` select Markdown. Anything else is `400`.
Missing save is `404`; missing athlete or season/league is `404`; corrupt sporting state is `400`.
Downloads are read-only: one `AsNoTracking` context in one committed read transaction
for a consistent snapshot across concurrent simulation commits, no `SaveChanges`, no RNG mutation. Provisional
standings reuse the live-table kernel with a discarded RNG fork, exactly like `CurrentStandings`.
Corrupt stage cursors abort via `GlobalStageGate` and surface as `400`; reports never
substitute a synthetic stage value.

## Contract (v1)

JSON envelope: `schemaVersion: 1`, `reportType` (`athlete-profile` | `standings-table` | `cup-edition` | `cup-team` | `league-profile` | `season-dossier` | `universe-almanac`), `saveId`,
`generatedUtc`, `save { name, currentSeason, phase, schemaVersion, rulesVersion }`,
scope ids, `snapshot` phase/season/stage info, typed `data`, plus a `units` block.
Markdown carries the same scope/status/metadata with headings and tables; it never embeds JSON.

MSS-088 packs reuse the same versioned contract. The season dossier covers exactly one
season: full Superleague table, stage-by-stage winners, concise feeder summaries,
movement/qualifier status, completed Cup summaries (Type qualification vs Final and
Color individual vs team kept separate), defending-champion context, leader/Cup-performer
career titles and official career records. The universe almanac is the all-time companion:
season champions/podiums, Color/Type Cup medal history, official records, weighted Career
Leaders plus official Superleague points leaders as a separate statistic, and
consecutive-title streaks. Both carry an explicit snapshot/coverage header and a stale
warning; incomplete or not-yet-held sections are omitted, never inferred.

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
- Zones (final only): Superleague `safe/qualifier/relegation`; tiered Feeder 1
  `champion/qualifier-up/safe/qualifier-hold/relegated`; Feeder 2
  `promoted/qualifier-up/qualifier-hold/relegated`; Feeder 3
  `promoted/qualifier-up/safe`. Historical v1 single-feeder rows (division 0,
  surfaced as Feeder 1) keep their original bands `champion/qualifier/safe`
  (1 champion promoted, 2–4 qualifier challengers, 5–32 safe), matching the
  Standings UI legacy mapping. Provisional rows omit zones.

## Files

- `athlete-{id}-{slug}.md|json`, `standings-season-{season}-league-{leagueId}-{slug}.md|json`,
  `cup-{cup}-season-{season}-edition.md|json`, `cup-{cup}-team-{slug}.md|json`,
  `league-{key}-profile.md|json`, `season-{season}-dossier.md|json`,
  `universe-almanac-{save-slug}.md|json`
  (ASCII slug; full UTF-8 names stay in the body).
- `Content-Type: text/markdown; charset=utf-8` or `application/json; charset=utf-8`,
  `Content-Disposition: attachment`.

## ChatGPT workflow (MSS-088)

- Export the almanac occasionally as a stable history reference.
- Export a fresh season dossier whenever new coverage is needed.
- Attach both files to a ChatGPT Project or chat; replace the dossier as the save advances.
- The app never syncs projects automatically; uploads become stale as simulation advances.
