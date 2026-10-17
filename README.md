# OPCAgentSample
[![CI](https://github.com/vompa/OPCAgentSample/actions/workflows/ci.yml/badge.svg)](https://github.com/vompa/OPCAgentSample/actions/workflows/ci.yml)

**Maschinendaten in natürlicher Sprache abfragen: OPC UA → MCP-Server → Claude.**

Referenz-Sample für eine simulierte Produktionshalle für Schleifmittel (4 Rundtisch-Pressen, 2 Aushärteöfen,
2 Verpackstellen, 3-Schicht-Betrieb). Ein OPC-UA-Simulator liefert Live-Werte und eine reproduzierbare Historie,
ein MCP-Server verdichtet sie mit Wartungs-, Auftrags- und Stammdaten zu deterministischen Kennzahlen, und Claude
beantwortet die Fragen von Schichtleiter, Hallenleiterin und Produktionsleiter.

> **Ein Dashboard zeigt, *dass* etwas ist. Die KI erklärt, *warum* und *was zu tun ist* – auf Basis derselben
> verlässlichen Daten.**

Status: **im Aufbau.** Die vollständige Spezifikation steht in [docs/spec.md](docs/spec.md), der freigegebene
Entwurf der Oberfläche in [docs/mockup/](docs/mockup/), die Kalibrierung des Szenarios und alle Kennzahl-Definitionen in [docs/calibration.md](docs/calibration.md).

## Grundsätze

- **Nur lesend:** keine Steuerbefehle an Maschinen (kein Start/Stop, keine Sollwerte).
- **Kennzahlen und Ampeln rechnet der Server deterministisch**, nie die KI. Claude wählt Tools, verknüpft Ergebnisse
  und formuliert.
- **Rollen-Scoping:** Jede Rolle sieht nur ihre Daten; keine personenbezogene Leistungsauswertung.
- **Nachvollziehbar:** Jede Antwort nennt Quellen und Datenstand, jeder Tool-Aufruf wird protokolliert.

## Aufbau

```
src/
  OPCAgentSample.Domain      Stationen, Schichtkalender, logische Zeitbereiche, Kategorien
  OPCAgentSample.Simulation  Szenario und Historien-Generator (fester Seed, reproduzierbar)
  OPCAgentSample.Simulator   OPC-UA-Server (Informationsmodell, HistoryRead, Alarme)
  OPCAgentSample.Rollup      Speicherbibliothek: verdichtete Zeitbereiche in Redis (Schlüssel, Codec, Schreiber, Leser, Kaskade)
  OPCAgentSample.Server      Collector, Kennzahlen, MCP-Server, Web-Demo (Redis als einziger Speicher)
tests/
  OPCAgentSample.Tests       Unit-, Integrations- und Referenzantwort-Tests
```

Datenhaltung: **Redis ist der einzige Speicher.** Es hält den aktuellen Zustand, die Meldungen und die verdichteten Zeitbereiche
(Schicht, Tag, Woche, Monat, Quartal, Jahr) mit fester Aufbewahrung; die Daten sind flüchtig und entstehen bei jedem Start aus dem Seed
(Details: [docs/spec.md](docs/spec.md#15-datenhaltung-redis)).

## Lokal starten

Voraussetzungen: .NET 10 SDK, Docker.

```
docker compose up -d                                    # Redis 7
dotnet run --project src/OPCAgentSample.Simulator       # OPC-UA-Simulator auf opc.tcp://localhost:4840
dotnet run --project src/OPCAgentSample.Server          # Server auf http://localhost:5145, Collector verbindet sich mit dem Simulator
```

Der Server holt beim ersten Start die Historie ab Mo 05.10.2026 per HistoryRead aus dem Simulator nach (ca. eine Minute,
1,5 Mio. Datensätze) und folgt danach per Subscription. Seine Uhr ist die Simulationsuhr des Simulators
(`Werk.Simulation.Zeit`, Standard Do 15.10.2026 09:42); mit `-- --speed 60 --start 2026-10-15T09:42` am Simulator läuft sie
schneller. Läuft der Simulator nicht, versucht der Collector die Verbindung mit wachsender Wartezeit erneut; nach einem
Ausfall lädt er die Lücke nach. Zum Prüfen: `curl http://localhost:5145/api/health` (`{"status":"healthy"}`).

Der Collector wird in `appsettings.json` (Abschnitt `Opc`) konfiguriert: `Opc:Endpoint` (Standard `opc.tcp://localhost:4840`),
`Opc:UseSecurity` (signieren und verschlüsseln), `Opc:AutoAcceptUntrustedCertificates` (nur in `appsettings.Development.json`
an: sonst muss das Serverzertifikat in `pki/collector/trusted` liegen) und `Opc:Enabled` (aus: kein Collector, die Demo-Uhr
bleibt die Referenzuhr).

Beim Start spielt der Server die Stammdaten aus `src/OPCAgentSample.Server/Seed/*.json` nach Redis ein (`md:*`). Die einzige
Verbindung steht in `appsettings.json` (`ConnectionStrings:Redis`) und passt zur `docker-compose.yml`. Redis läuft ohne
Persistenz und ohne Verdrängen (`--save "" --appendonly no --maxmemory-policy noeviction`): Beim Neustart oder nach dem Leeren
baut der Server alles aus dem Seed und der Historie des Simulators neu auf. Fehlt Redis oder sind die Stammdaten weg,
antwortet `/api/health` mit 503 (`unhealthy`), sonst mit 200 (`healthy`); einen Zwischenzustand `degraded` gibt es nicht.
Aufräumen: `docker compose down`.

Tests starten nur noch Redis selbst über Testcontainers (Docker nötig): `dotnet test OPCAgentSample.slnx -c Release`.

## Claude anbinden

Ohne Key beantwortet die Referenz-Engine nur die vorbereiteten Fragen; jede Karte ist als „Vorbereitete Antwort – Claude ist nicht angebunden“ gekennzeichnet, und das Start-Log nennt den Modus. Mit einem Key übernimmt `ClaudeAnswerEngine` (Modell `claude-haiku-5-5`):

```
dotnet user-secrets set Claude:ApiKey "<dein Key>" --project src/OPCAgentSample.Server    # lokal
# oder als Umgebungsvariable: Claude__ApiKey
```

Der Key gehört nie in `appsettings.json` oder ins Repo. Claude ruft die 17 Tools des eigenen MCP-Servers mit der Rolle des Fragenden auf (Rollen-Scoping und Audit-Log wie bei externem Zugriff) und liefert die Antwortkarte als geprüftes JSON; Datenstand und Quellen setzt der Server aus den Tool-Ergebnissen. Einstellungen im Abschnitt `Claude` (Effort, Grenzen für Runden und Tool-Aufrufe, Tageskontingent je Besitzer und insgesamt).

**Schneller und sparsamer:** Der Modellaufruf streamt; die Überschrift und das „Was tun?“ der Antwortkarte erscheinen, sobald Claude sie geschrieben hat (SSE-Ereignis `answer_draft`), der Rest folgt nach der Prüfung. Dieselbe Frage desselben Besuchers in derselben Rolle wird 5 Minuten lang aus dem Speicher beantwortet (`Claude:RepeatWindowSeconds`, 0 = aus): sofort, ohne Tokens und ohne Kontingent, gekennzeichnet als „Wiederholung“; „Neu fragen“ umgeht das. Details: [docs/spec.md](docs/spec.md) Abschnitt 17.

**Kosten:** Jede Frage ruft die kostenpflichtige Claude-API auf (mehrere Modellaufrufe mit Tools). Das Tageskontingent (`Claude:DailyQuestionsPerOwner`, `DailyTokensPerOwner`, `DailyQuestionsTotal`) begrenzt das; setze zusätzlich ein Ausgabenlimit in der Anthropic Console. Die Tests rufen die API nie auf.

## Weboberfläche

Vue 3 + TypeScript (Vite) als installierbare PWA in `src/OPCAgentSample.Web` (Pinia, Vitest, ESLint, Prettier; Node 22, siehe `.nvmrc`).
Der Build geht nach `src/OPCAgentSample.Server/wwwroot` (nicht im Repo; die CI baut es vor `dotnet build`), der Server liefert es aus:

```
cd src/OPCAgentSample.Web
npm ci && npm run build      # Typprüfung + Build nach ../OPCAgentSample.Server/wwwroot
npm run dev                  # Vite-Entwicklungsserver auf :5173, /api geht an http://localhost:5145 (VITE_DEV_PROXY ändert das Ziel)
npm run test && npm run lint
```

Ohne gebautes `wwwroot` weist `dotnet build` per Meldung auf `npm run build` hin (npm wird nicht automatisch aufgerufen).

- **Aufbau:** `src/api` (typisierte API-Schicht, SSE-Parser), `src/live` (`LiveConnection` mit `EventSource` oder `fetch`, Wiederverbinden mit `Last-Event-ID`), `src/stores` (Pinia je Bereich), `src/components`, `src/platform` (`SpeechInput`, `KeyValueStorage`, `PushChannel`).
- **Reports:** Menüpunkt „Reports“ (Statusleiste bzw. untere Navigation) zeigt eine eigene Seite aus `GET /api/reports/summary`: Kennzahl-Kacheln, Histogramm der Antwortzeiten, Verläufe der letzten 24 Stunden, MCP-Werkzeuge und Datenaufnahme (SVG-Komponenten ohne Diagrammbibliothek, Tabellen-Alternative zu jedem Diagramm, Aktualisierung alle 30 s). Screenshots: [Desktop](docs/img/reports-desktop.png), [Handy](docs/img/reports-mobile.png).
- **Maschinen-Report:** Unter „Reports“ zeigt der Reiter **Maschinen** (neben „KI-Nutzung“) die acht Stationen der Halle A für die laufende Schicht, gestern, diese Woche oder die letzten vier Wochen, optional mit Vorperiode (Start je Rolle: Schichtleiter Schicht, Hallenleiterin Woche, Produktionsleiter vier Wochen mit Kosten). Raster mit OEE-Ampel, Ist/Soll, Ausschuss, Stillstand und Trendpfeil; je Station OEE zerlegt, Stillstands-Pareto in Minuten und Euro, Ausschuss nach Grund, Prozesswerte mit Toleranzband und Anteil außerhalb, MTBF/MTTR, Reaktionszeit der Instandhaltung, Formstandzeit und Wartung; dazu Schichtvergleich mit Anlaufverlusten und Kosten der Verluste. An jeder Auffälligkeit stellt **Mit Claude analysieren** eine passende Frage in der Fragen-Ansicht; die Frage verweist auf die Auffälligkeit, und der Server gibt Claude Zeitraum, Station und Report-Werte als Kontext mit (Details: [docs/spec.md](docs/spec.md), Abschnitt 17). `GET /api/reports/machines?role=&range=&compare=` rechnet mit denselben Funktionen wie die MCP-Werkzeuge (keine zweite Rechnung, keine Personendaten, 30 s zwischengespeichert); in der Vorschau liegt der Report der Rolle im Standardzeitraum in der Momentaufnahme. Screenshots: [Desktop](docs/img/reports-machines-desktop.png), [Handy](docs/img/reports-machines-mobile.png).
- **CSP:** nur `'self'` (keine Inline-Skripte, kein `eval`, Schriften lokal über `@fontsource`, keine Frames).
- **Capacitor-Vorbereitung:** `VITE_API_BASE` setzt die API-Basisadresse; `setAuthToken` sendet `Authorization: Bearer …` (dann nutzt der Live-Strom `fetch` statt `EventSource`); Plattformdienste sind austauschbar (`configurePlatform`). Capacitor selbst ist nicht eingerichtet.
- **PWA / Web-Push:** Manifest, Service Worker (Offline-Hülle, `/api` nie im Cache) und Push-Handler sind vorhanden; Schnittstelle `PushChannel` ist vorbereitet. Offen (Server): VAPID-Schlüssel erzeugen, Abo-Endpunkt und Tabelle für `PushSubscription`, Versand bei neuen dringenden Postfach-Fällen (z. B. mit `Lib.Net.Http.WebPush`), öffentlicher Schlüssel als `VITE_VAPID_PUBLIC_KEY`.

## Beobachtbarkeit

Server und Simulator sind mit **OpenTelemetry** instrumentiert (Traces, Metriken und Logs). Der Export per OTLP ist **standardmäßig aus**:
Es wird nichts gesendet, und ein Empfänger ist nicht nötig. Die Betriebs- und Nutzungszahlen zeigt die Report-Seite der Weboberfläche.

- **Export einschalten:** `Observability:OtlpEnabled=true` (Server und Simulator, z. B. `--Observability:OtlpEnabled=true` oder die
  Umgebungsvariable `Observability__OtlpEnabled=true`) und die Adresse eines beliebigen OTLP-Empfängers in
  `OTEL_EXPORTER_OTLP_ENDPOINT` (Standard `http://localhost:4317`, gRPC). Ohne erreichbaren Empfänger läuft alles normal weiter; die
  Telemetrie wird verworfen.
- **Weboberfläche:** Der Menüpunkt **Reports** rechnet aus den Nutzungszählern in Redis (`usage:*`) und den Zuständen des Servers
  (`GET /api/reports/summary`, kurz zwischengespeichert; die Preise der Kostenschätzung stehen in `Reports:Prices`, Standard Haiku 5.5:
  0,10 / 0,50 USD je Mio. Tokens Eingabe/Ausgabe, Cache lesen 0,01, Cache schreiben 0,125). Die Kosten sind eine **Schätzung** und
  ersetzen keine Abrechnung. Die Zähler „seit Serverstart“ (Kontingent-Ablehnungen, aufgenommene Datensätze) beginnen mit jedem Start neu.
- **Gemessen wird** (Meter `OPCAgentSample`): `mcp.tool.calls`, `mcp.tool.duration`, `claude.questions`, `claude.question.duration`,
  `claude.tokens`, `claude.quota.denials`, `collector.lag` (Simulationsuhr minus Wasserstand), `ingest.records`,
  `live.sse.connections`, `kpi.blocks`, dazu Laufzeit-, ASP.NET-Core- und HttpClient-Metriken. Spans: ASP.NET Core, HttpClient,
  Redis (`redis.command`), `collector.ingest_window`, `mcp.tool`, `claude.question`, `claude.model_call`.
- **Datenschutz:** Die Telemetrie enthält keine Frage- oder Antworttexte, Tool-Argumente oder Schlüssel. Spans und
  Metriken tragen nur Rolle, Tool-Name, Modell, Token-Zahlen und Ergebnis; Tests prüfen das.

## Live-Demo

Öffentlich unter **https://opc.vompa.dev** (Zugangscode auf Anfrage). Die Oberfläche liegt auf Azure Static Web Apps und ist immer sofort da;
das Backend (Container App mit Server, Simulator und Redis, flüchtige Daten aus dem Seed) unter `api.opc.vompa.dev` schläft und
wird **erst nach der Anmeldung** geweckt. Währenddessen zeigt die Oberfläche eine Vorschau aus einer eingecheckten Momentaufnahme
(`src/OPCAgentSample.Web/public/snapshot`, erzeugt mit `src/OPCAgentSample.Snapshot`) und wechselt auf Live-Daten, sobald das Backend antwortet.
Anmeldung mit Zugangscode oder persönlichem Einladungslink (`/#code=<key>`, siehe [infra/README.md](infra/README.md#einladungslinks)) und Login-Drossel, Verlauf je Browser-Sitzung und Rolle, Tageskontingent für Claude, Reports als eigene Seite der
Weboberfläche. Ablauf, Aufbau, Kosten, Einrichtung und Betrieb: [infra/README.md](infra/README.md).
Lokal lässt sich die App mit denselben Images nachstellen: [deploy/docker/compose.azure-local.yml](deploy/docker/compose.azure-local.yml).
Als App installieren (Windows, iOS, Android): [docs/install-pwa.md](docs/install-pwa.md).

**Erklärseite:** Wer ohne Sitzung kommt, sieht statt einer schlichten Anmeldung die Erklärseite (`AboutView`): Was die Demo ist, Weg einer Antwort von OPC UA bis zur Antwortkarte, Rollen mit Beispielfragen, Zahlen und Grenzen, Sicherheit und Ehrlichkeit, Zugang mit Code-Feld und Verweise auf das Projekt. Einladungslinks (`/#code=<key>`) melden weiterhin direkt an und überspringen sie. Angemeldet (und lokal ohne Demo-Zugang) öffnet „Über die Demo“ (Kopf am PC, Seitenleiste am Handy) dieselbe Seite mit Rückweg in die Demo. Die Bilder liegen als WebP mit PNG-Rückfall in `src/OPCAgentSample.Web/public/img/`.

## Herkunft und Lizenz

Bausteine (OPC-Session, API-Key mit Rollen, Audit, Rate Limit, Simulator-Gerüst) stammen aus
[OPCSample](https://github.com/vompa/OPCSample). [MIT](LICENSE)
