#!/bin/sh
# MSS-043 smoke check for the containerized app.
#
# Expects the Compose application to be already running
# (e.g. `docker compose up -d --build`). Polls with deterministic HTTP
# readiness retries (no arbitrary sleeps) and verifies:
#   1. GET /api/health returns 200.
#   2. GET /api/catalog/stats returns 200 with catalog-stats JSON
#      (no imported catalog or created save required).
#   3. A direct deep-linked SPA route (save-scoped dashboard) returns the
#      React shell (index.html fallback), proving static hosting + SPA
#      fallback work from the same host as /api.
#
# Usage: sh scripts/docker-smoke.sh [base_url] [timeout_seconds]
# Defaults: http://localhost:8080, 180s.

set -eu

BASE_URL="${1:-http://localhost:8080}"
TIMEOUT_SECONDS="${2:-180}"

fail() {
  echo "docker-smoke: FAIL: $1" >&2
  exit 1
}

echo "docker-smoke: waiting for ${BASE_URL}/api/health (up to ${TIMEOUT_SECONDS}s)..."
deadline=$((SECONDS + TIMEOUT_SECONDS))
until curl -fsS "${BASE_URL}/api/health" >/dev/null 2>&1; do
  if [ "$SECONDS" -ge "$deadline" ]; then
    fail "${BASE_URL}/api/health never became ready within ${TIMEOUT_SECONDS}s"
  fi
  sleep 2
done
echo "docker-smoke: /api/health is ready."

STATS="$(curl -fsS "${BASE_URL}/api/catalog/stats")" \
  || fail "GET /api/catalog/stats failed"
echo "docker-smoke: /api/catalog/stats -> ${STATS}"
case "${STATS}" in
  *otalAthletes*)
    echo "docker-smoke: catalog stats payload shape OK."
    ;;
  *)
    fail "GET /api/catalog/stats returned unexpected payload: ${STATS}"
    ;;
esac

# Save-scoped deep link with a synthetic GUID: no save exists, so the API
# is irrelevant here; the ASP.NET SPA fallback must serve the React shell
# (HTTP 200 containing the Vite root element), exactly as a browser refresh
# on a standings/athlete/history URL would request.
SPA_URL="${BASE_URL}/saves/00000000-0000-0000-0000-000000000000/dashboard"
SPA_HTML="$(curl -fsS "${SPA_URL}")" \
  || fail "GET ${SPA_URL} failed (SPA fallback broken)"
case "${SPA_HTML}" in
  *'id="root"'*)
    echo "docker-smoke: SPA deep-link fallback OK (${SPA_URL} serves the React shell)."
    ;;
  *)
    fail "SPA deep link did not return the React shell: ${SPA_URL}"
    ;;
esac

echo "docker-smoke: PASS (API + catalog stats + SPA fallback on ${BASE_URL})."
