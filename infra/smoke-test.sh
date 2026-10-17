#!/usr/bin/env bash
# Smoke-Test des Backends (API) der Live-Demo: Erreichbarkeit, Zugriffsschutz, CORS und (mit Zugangscode) Anmeldung, API und Reports.
# Die Oberfläche prüft infra/smoke-test-web.sh, ohne das Backend zu wecken.
#
#   infra/smoke-test.sh https://api.opc.vompa.dev
#   SMOKE_ACCESS_CODE=<code> SMOKE_WEB_ORIGIN=https://opc.vompa.dev infra/smoke-test.sh https://api.opc.vompa.dev
#   SMOKE_SERVES_UI=1 infra/smoke-test.sh http://localhost:8080      # lokaler Nachbau, der die Oberfläche selbst ausliefert
#
# Scale-to-zero: Die erste Anfrage weckt die App (Kaltstart mit Image-Abruf und Datenbankaufbau), deshalb wartet der
# Test bis zu zehn Minuten auf /api/health.
set -euo pipefail

readonly base="${1:?API-Adresse fehlt, z. B. https://api.opc.vompa.dev}"
# Herkunft der Oberfläche für die CORS-Prüfung (leer = nicht prüfen)
readonly web_origin="${SMOKE_WEB_ORIGIN:-}"
readonly cookies="$(mktemp)"
trap 'rm -f "$cookies"' EXIT

fail() {
  echo "FEHLER: $*" >&2
  exit 1
}

# Erwarteten HTTP-Status prüfen: expect <status> <methode> <pfad> [curl-Argumente ...]
expect() {
  local status="$1" method="$2" path="$3"
  shift 3
  local actual
  actual="$(curl -s -o /dev/null -w '%{http_code}' -X "$method" --max-time 60 "$@" "$base$path")"
  [[ "$actual" == "$status" ]] || fail "$method $path: erwartet $status, bekommen $actual"
  echo "ok  $method $path → $status"
}

started=$(date +%s)
for attempt in $(seq 1 60); do
  if curl -fsS --max-time 30 "$base/api/health" > /dev/null 2>&1; then break; fi
  [[ "$attempt" == 60 ]] && fail "/api/health antwortet nach zehn Minuten nicht"
  echo "Warte auf die App ($attempt/60) ..."
  sleep 10
done
echo "App bereit nach $(( $(date +%s) - started )) s"

expect 200 GET /api/health
# Reine API: Die Oberfläche liegt auf der Static Web App (Web:ServeUi=false). Im lokalen Nachbau mit Oberfläche: SMOKE_SERVES_UI=1.
if [[ "${SMOKE_SERVES_UI:-0}" == 1 ]]; then expect 200 GET /; else expect 404 GET /; fi
curl -fsS --max-time 30 "$base/api/demo/session" | grep -q '"enabled":true' || fail "Demo-Zugang ist nicht eingeschaltet"
echo "ok  GET /api/demo/session → Demo an"

if [[ -n "$web_origin" ]]; then
  # CORS: Vorabfrage der Oberfläche muss mit Cookie erlaubt sein, fremde Herkunft nicht
  headers="$(curl -s -D - -o /dev/null -X OPTIONS --max-time 30 -H "Origin: $web_origin"     -H 'Access-Control-Request-Method: POST' -H 'Access-Control-Request-Headers: content-type' "$base/api/demo/login")"
  grep -qi "^access-control-allow-origin: $web_origin" <<< "$headers" || fail "CORS: $web_origin ist nicht erlaubt"
  grep -qi '^access-control-allow-credentials: true' <<< "$headers" || fail "CORS: Credentials nicht erlaubt"
  foreign="$(curl -s -D - -o /dev/null -X OPTIONS --max-time 30 -H 'Origin: https://fremd.example'     -H 'Access-Control-Request-Method: POST' "$base/api/demo/login")"
  ! grep -qi '^access-control-allow-origin:' <<< "$foreign" || fail "CORS: fremde Herkunft wird erlaubt"
  echo "ok  CORS nur für $web_origin"
fi

# Ohne Sitzung: API gesperrt, MCP nur mit API-Key
expect 401 GET /api/me
expect 401 GET /api/reports/summary
expect 401 GET '/api/reports/machines?role=shift_lead'
expect 401 POST /mcp -H 'Content-Type: application/json' -d '{}'
expect 401 POST /api/demo/login -H 'Content-Type: application/json' -d '{"code":"falscher-code"}'

if [[ -n "${SMOKE_ACCESS_CODE:-}" ]]; then
  # JSON ohne jq (lokal nicht überall installiert): Backslash und Anführungszeichen maskieren
  escaped="${SMOKE_ACCESS_CODE//\\/\\\\}"
  escaped="${escaped//\"/\\\"}"
  body="{\"code\":\"$escaped\"}"
  expect 200 POST /api/demo/login -H 'Content-Type: application/json' -d "$body" -c "$cookies"
  expect 200 GET /api/me -b "$cookies"
  expect 200 GET /api/overview -b "$cookies"
  expect 200 GET /api/reports/summary -b "$cookies"
  # Die Report-Seite zeigt nur Zähler: Die Antwort enthält die Gruppen der Zusammenfassung
  curl -fsS --max-time 30 -b "$cookies" "$base/api/reports/summary" | grep -q '"latencyHistogram"' || fail "/api/reports/summary liefert keine Zusammenfassung"
  echo "ok  GET /api/reports/summary → Zusammenfassung"
  # Maschinen-Report: acht Stationen der Halle A im Standardzeitraum der Rolle
  expect 200 GET '/api/reports/machines?role=hall_lead' -b "$cookies"
  curl -fsS --max-time 60 -b "$cookies" "$base/api/reports/machines?role=hall_lead" | grep -q '"range":"week"' || fail "/api/reports/machines liefert nicht den Wochenreport der Hallenleiterin"
  echo "ok  GET /api/reports/machines → Wochenreport"
  expect 204 POST /api/demo/logout -b "$cookies" -c "$cookies"
else
  echo "Hinweis: SMOKE_ACCESS_CODE nicht gesetzt, Anmeldung und Reports nicht geprüft."
fi

echo "Smoke-Test bestanden: $base"
