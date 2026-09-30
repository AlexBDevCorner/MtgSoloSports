# Frontend routing (MSS-040)

Every main page and athlete profile has a stable, copyable browser URL.
Navigation uses semantic anchors (`Link` in `features/routing/router.tsx`);
buttons remain for actions (simulate, reveal, import) and selectors.

## Route shapes

- `/` — root resolver: redirects (replace) to the last-opened save dashboard
  when `localStorage:mtg-solo-sports:selected-save` exists, else to `/saves`.
- `/saves` — save management.
- `/saves/:saveId/dashboard`
- `/saves/:saveId/live?league=<id>&round=<n>`
- `/saves/:saveId/history?season=<n>&competition=<id>&stage=<n>&round=<n>`
- `/saves/:saveId/records`
- `/saves/:saveId/cups`
- `/saves/:saveId/athletes/:athleteId`

Helpers in `features/routing/routes.ts` build and parse every shape; pages
share them instead of hand-rolling URLs.

## Query scheme

- Live: `league` is authoritative when it names a league in the save,
  otherwise per-save stored league then first league. `round` is authoritative
  when it names a persisted round, otherwise the latest round. Changing the
  league pushes a new league URL; picking a round or advancing pushes the
  round URL. Back/Forward syncs both.
- History: `season`, `competition` (league id), `stage`, `round` restore that
  exact view when valid, otherwise the latest available entry at each level.
  Changing a selector pushes the updated history URL.
- Live event mode: `event` (`qualifier`, `color-cup-individual`,
  `color-cup-team`, `type-cup-team`), `season` (source season) and `group`
  (team events) select a postseason event played round by round; `round`
  selects a played round (latest when absent). Without `event`, Live shows the
  event when it is the next lifecycle step, otherwise leagues. The URL keeps
  the event after it completes, so the completed event stays on screen.
- History postseason: `event` and `group` (with `season` and `round`) replay a
  postseason event's stored rounds; unknown event keys are ignored.

No navigation resets the selected persisted Live round unexpectedly and no
reveal autoplays on navigation; reveal state stays presentation-only.

## Save scoping and errors

- The route save id is authoritative for all requests and page context.
- `last-selected-save` is only a fallback for `/`; explicit save-scoped URLs
  never read it and no tab subscribes to storage events, so tabs stay
  independent.
- Deleted/nonexistent saves render a recoverable “Save unavailable” notice
  with a link to `/saves`, never a blank page or another save’s data.
- Malformed athlete ids (non-positive integers) render a recoverable
  “Invalid athlete link” notice with links to dashboard/live.

## Hosting

- Vite dev serves the SPA entry for UI routes and proxies `/api`.
- ASP.NET Core serves `wwwroot` plus `MapFallbackToFile("index.html")` for UI
  deep links; unknown `/api/*` returns a JSON 404 before that fallback so
  server errors are unaffected.

## Future extensions

New save-scoped pages must use `/saves/:saveId/<area>` and reuse the
builders. Reserved for MSS-041:

- `/saves/:saveId/leagues/:leagueId` via `leagueStandingsPath(saveId, leagueId)`
  — first-class league standings detail. MSS-040 documents the builder only;
  the parser treats it as notFound until MSS-041 implements the page.

## Manual evidence (MSS-040 run)

ASP.NET Core built UI (`dotnet run`, `wwwroot` from `npm run build`):

- `GET /api/health` → `200`.
- `GET /saves` → `200 text/html` (SPA entry).
- `GET /saves/11111111-1111-1111-1111-111111111111/live?league=7` → `200 text/html`.
- `GET /saves/abc/athletes/42` → `200 text/html`.
- `GET /api/unknown-route-xyz` → `404 application/json {"error":"Unknown API route."}`
  (fallback never masks server errors).

Frontend unit evidence: `npm run test` covers route parse/build round-trips,
invalid athlete/query handling, the reserved MSS-041 path, and source checks
that shell/athlete navigation uses real links with native new-tab behavior
and route-authoritative save ids.

Browser checklist (per release, Vite dev + built UI):

1. Copy a profile URL, paste in a fresh tab, reload — same athlete/save.
2. Ctrl/Cmd-click and middle-click a card name — opens the correct profile in
   a new tab; two profiles plus Live/History coexist in different tabs.
3. Reload `/saves/:saveId/live?league=X&round=Y` and the matching history URL
   — same rounds, no resimulation.
4. Back/Forward across Dashboard/Live/History/profile — address bar matches.
5. Open save A in tab 1 and save B in tab 2 — neither tab redirects the other.
6. Open a deleted save id and `/saves/abc/athletes/xyz` — recoverable notices.
