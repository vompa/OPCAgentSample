#!/usr/bin/env bash
# Smoke-Test der Oberfläche (Static Web App): rein statisch, sendet KEINE Anfrage an das Backend und weckt es nicht.
#
#   infra/smoke-test-web.sh https://opc.vompa.dev https://api.opc.vompa.dev
#
# Geprüft werden Startseite, SPA-Fallback, Momentaufnahme (Vorschau-Daten), Service Worker, Sicherheitsköpfe und dass die
# CSP die API-Adresse erlaubt.
set -euo pipefail

readonly web="${1:?Adresse der Oberfläche fehlt, z. B. https://opc.vompa.dev}"
readonly api="${2:?API-Adresse fehlt, z. B. https://api.opc.vompa.dev}"

fail() {
  echo "FEHLER: $*" >&2
  exit 1
}

# Static Web Apps sind nach dem Ausrollen mitunter einige Sekunden nicht erreichbar: wiederholen.
fetch() { curl -fsS --retry 6 --retry-delay 10 --retry-all-errors --max-time 30 "$@"; }

headers="$(fetch -D - -o /dev/null "$web/")" || fail "$web/ nicht erreichbar"
echo "ok  GET / → 200"
csp="$(grep -i '^content-security-policy:' <<< "$headers" || true)"
[[ -n "$csp" ]] || fail "Content-Security-Policy fehlt"
grep -q "connect-src 'self' $api" <<< "$csp" || fail "CSP erlaubt die API-Adresse $api nicht (connect-src)"
grep -qi '^x-content-type-options: nosniff' <<< "$headers" || fail "X-Content-Type-Options fehlt"
grep -qi '^referrer-policy: no-referrer' <<< "$headers" || fail "Referrer-Policy fehlt"
echo "ok  Sicherheitsköpfe, CSP erlaubt $api"

fetch "$web/" | grep -q '<div id="app">' || fail "Startseite ist nicht die Oberfläche"
fetch -o /dev/null "$web/irgendeine/seite" || fail "SPA-Fallback fehlt"
echo "ok  SPA-Fallback"

for role in shift_lead hall_lead production_lead; do
  fetch "$web/snapshot/$role.json" | grep -q '"version":"1"' || fail "Momentaufnahme $role fehlt oder ist unlesbar"
done
fetch "$web/snapshot/reports.json" | grep -q '"latencyHistogram"' || fail "Beispiel-Reportdaten fehlen"
echo "ok  Momentaufnahme (3 Rollen, Reports)"

fetch -o /dev/null "$web/sw.js" || fail "Service Worker fehlt"
fetch -o /dev/null "$web/manifest.webmanifest" || fail "Manifest fehlt"
echo "ok  Service Worker und Manifest"

echo "Smoke-Test Oberfläche bestanden: $web"
