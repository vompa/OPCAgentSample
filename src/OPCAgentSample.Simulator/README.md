# OPCAgentSample.Simulator

OPC-UA-Server, der die simulierte Schleifmittel-Halle (Spezifikation `docs/spec.md`, Abschnitte 3, 4, 5, 15) ausliefert.
Er ist die „Maschinenseite“ des Samples: Der Collector des MCP-Servers liest hier per **HistoryRead** die Historie ab
Mo 05.10.2026 00:00 nach und folgt danach per **Subscriptions** und **Alarms & Conditions**.

**Der Simulator ist rein lesend.** Es gibt keine schreibbaren Variablen und keine Steuer-Methoden. Auch die Methoden der
Alarm-Conditions (Acknowledge, Confirm, AddComment, Enable, Disable, Suppress …) werden mit `BadUserAccessDenied`
abgewiesen. Das ist eine Kernaussage der Demo und durch Tests abgesichert. Die Simulationsuhr lässt sich nur lokal über
Kommandozeile oder Konfiguration steuern, nie über OPC.

## Start

```powershell
dotnet run --project src/OPCAgentSample.Simulator
dotnet run --project src/OPCAgentSample.Simulator -- --speed 60 --start 2026-10-15T09:42
```

Der Start dauert ca. 2 s (Einspielen der Historie bis zur Startzeit). Strg+C beendet den Server sauber.

| Option | Konfiguration (`Simulator:`) | Standard | Bedeutung |
|---|---|---|---|
| `--bind` | `BindAddress` | 127.0.0.1 | Adresse, an die der Server bindet (nur Loopback, keine Firewall-Abfrage); im Container `0.0.0.0`. Mit dem Hostnamen `localhost` bindet das OPC-SDK an alle Netzwerkkarten |
| `--port` | `Port` | 4840 | TCP-Port, Endpunkt `opc.tcp://<BindAddress>:<Port>` |
| `--speed` | `Speed` | 1 | Simulationsgeschwindigkeit: 1 = Echtzeit, 60 = eine Minute je Sekunde, 0 = Uhr angehalten |
| `--start` | `Start` | Do 15.10.2026 09:42 | Startzeit der Simulationsuhr; ohne Zone gilt Ortszeit Wien, sonst `Z` oder Offset; nicht vor Mo 05.10.2026 00:00 |
| `--interval` | `UpdateIntervalMs` | 1000 | Aktualisierungstakt der Live-Werte in ms Realzeit |
| `--seed` | `Seed` | 4211 | Seed des Historien-Generators (Standard = Demo) |
| `--pki` | `PkiPath` | `pki/simulator` | Zertifikatsordner (relativ zum Arbeitsverzeichnis, in `.gitignore`) |
| `--security` | `EnableSecurity` | true | zusätzlich `Basic256Sha256` (SignAndEncrypt) anbieten |
| – | `MaxValuesPerRead` | 5000 | Werte je Knoten und HistoryRead-Antwort, darüber folgt ein ContinuationPoint |

Sicherheit: `SecurityPolicy None` und `Basic256Sha256` mit selbstsigniertem Zertifikat (`pki/simulator/own`), nur anonyme
Anmeldung. Der Server nimmt Client-Zertifikate ohne Rückfrage an. Ein Client muss das Server-Zertifikat in seinen
Trust-Ordner legen oder es automatisch akzeptieren (Hinweis aus OPCSample: Sobald ein `CertificateValidation`-Handler
registriert ist, entscheidet er allein, `AutoAcceptUntrustedCertificates` allein genügt dann nicht).

In Tests und anderen Programmen: `await using var host = await SimulatorHost.StartAsync(options, logger, ct);`

## Namespace und NodeId-Schema (Vertrag für den Collector)

- Namespace-URI `urn:vompa:opc-agent-sample:plant`, Index **2**. Der Index wird beim Start geprüft (sonst bricht der Start ab).
- Alle NodeIds sind **String-NodeIds** `ns=2;s=<Pfad>`, lesbar, ASCII (ohne Umlaute) und zur Laufzeit stabil.
  Das Schema ist als gemeinsamer Vertrag von Simulator und Collector in `src/OPCAgentSample.Domain/Opc` festgelegt (Klassen `NodeIdScheme`, `StationGroups`, `StationVariables`, `EventVariables`; welche Knoten historisiert sind und auf welche Datensätze sie abgebildet werden: `HistoryNodeMap`).
- Browse-Namen sind die Pfadbestandteile (Namespace 2).

```
Station:   Halle<Id>.<Bereich>.<Station>                      HalleA.Pressen.RT-04
Gruppe:    <Station>.<Gruppe>                                 HalleA.Pressen.RT-04.Prozess
Variable:  <Station>.<Gruppe>.<Variable>                      HalleA.Pressen.RT-04.Prozess.PresskraftAufbauzeit
Alarm:     <Station>.Alarme.<Alarm-Id>                        HalleA.Pressen.RT-04.Alarme.A-0941
Klima:     HalleA.Klima.<Variable>                            HalleA.Klima.Temperatur
Kalender:  Schichtkalender.<Variable>                         Schichtkalender.AktuelleSchicht
Uhr:       Werk.Simulation.<Variable>                         Werk.Simulation.Zeit
Hallen B/C HalleB.Zaehler.<Woche>.<Schichtcode>.<Variable>    HalleB.Zaehler.2026-W42.F.StueckGut
Typen:     Typen.<Typ>                                        Typen.RundtischType
```

Bereiche: `Pressen` (RT-01 … RT-04), `Puffer` (PU-OFEN), `Oefen` (OF-01, OF-02), `Verpackung` (VP-01, VP-02).
Schichtcodes `F` (Früh), `S` (Spät), `N` (Nacht).

## Adressraum

```
Objects/
└─ Werk/
   ├─ Simulation        Zeit (Simulationsuhr, UTC), Geschwindigkeit, HistorieBeginn
   ├─ Schichtkalender   AktuelleSchicht, SchichtSchluessel, Beginn, Ende, Produktionstag, InProduktion
   ├─ HalleA/
   │  ├─ Klima          Temperatur, Luftfeuchte, Sollbereich (TemperaturMin/Max, LuftfeuchteMin/Max), ImSollbereich
   │  ├─ Pressen/       RT-01 … RT-04   (RundtischType)
   │  ├─ Puffer/        PU-OFEN         (PufferType)
   │  ├─ Oefen/         OF-01, OF-02    (OfenType)
   │  └─ Verpackung/    VP-01, VP-02    (VerpackungType)
   └─ HalleB/, HalleC/  nur Zustand (Betriebszustand) und verdichtete Wochenzähler je Schichtart
Typen (NodeIds Typen.<Name>), Untertypen von BaseObjectType bzw. Enumeration:
   StationType (Basis), RundtischType, OfenType, PufferType, VerpackungType
   Enums: Betriebszustand, Ausschussgrund
```

### Typen und Variablen

`StationType` ist die Basis aller Stationen. Die Gruppen `Auftrag`, `Zaehler`, `Energie` sind darin *Optional*; Rundtische und
Verpackungen haben sie, Öfen und Puffer nicht. Einheiten stehen als `EngineeringUnits` (EUInformation, UNECE-Codes) an den
Variablen. „H“ = historisiert (HistoryRead), Quellen siehe Abschnitt Historie.

| Typ | Gruppe: Variablen |
|---|---|
| **StationType** | `Identitaet`: StationId, Bezeichnung, Bereich, Hersteller, Baujahr · `Zustand`: Betriebszustand (Enum, H), ZustandSeit, MachineryItemState · `Auftrag`: AuftragsNr, ArtikelNr, SollMenge, IstMenge, SollTakt (s), PlanmengeSchicht · `Zaehler`: StueckGut (H), StueckAusschuss (H), Betriebsstunden (h), ZyklenGesamt · `Energie`: KwhSeitSchichtbeginn (kWh, H) · `Ereignisse`: JSON-Variablen (H) · `Alarme`: AktiveAlarme, AktiveMeldungen und die Conditions |
| **RundtischType** | `Prozess`: Taktzeit (s, H), TaktzeitMittel5Min, TaktzeitSoll, Presskraft (kN, H), PresskraftAufbauzeit (s, H), Presszeit (s), Formtemperatur (°C, H), Fuellgewicht (g, H), FuellgewichtMittel, FuellgewichtSoll, FuellgewichtToleranz, Dicke (mm, H) · `Zyklus` (je Takt, alle H): SollTaktzeit, AuftragsNr, ArtikelNr, MischungChargenId, Ausschussgrund (Enum) · `Teilstationen`: je Gewebezufuehrung1/Gewebezufuehrung2 (= Greifer 1/2), Dosierung, Abstreifer, Etikett, Presse, Ausstoss die Variablen `<Name>Zustand` (OK/Störung) und `<Name>Stoerzaehler` · `Material`: Mischung (Rezept), ChargenId, FuellstandKg (H), VerarbeitbarBis, Gewebemagazin (H), Etikettenmagazin (H) · `Werkzeug`: FormId, HuebeSeitWechsel, Standzeitgrenze · `Hydraulik`: Oeltemperatur (H), Systemdruck (bar, H) · `Ausschussgruende` (Schicht): Gewicht, Dicke, Gewebe, Etikett, Bruch |
| **OfenType** | `Kapazitaet`: Chargenplaetze, BelegtePlaetze (H), Belegung (% seit Schichtbeginn), BelegungAktuell (%) · `LaufendeCharge`: ChargenId, Durchmesser (mm), Ofenplatten, Profilschritt (Aufheizen/Halten/Abkühlen), RestzeitMin · `Temperatur`: Zone1Ist … Zone3Ist (H), Zone1Soll … Zone3Soll · `Planung`: NaechsteFreigabe, **Zuordnungsregel** · `Sicherheit`: TuerverriegelungZustand, LetztePruefung |
| **PufferType** | `Bestand`: Kapazitaet, AnzahlPlatten (H), MittlereWartezeit (min, H), AeltesteWartezeit (min, H) |
| **VerpackungType** | `Verpackung`: Kartontyp, StueckJeKarton, KartonsIst, KartonsSoll, KontrollwaageFehlmengen (H), Kartonmagazin (H), Etikettenmagazin (H), UmreiferZustand |

Die **Zuordnungsregel** des Ofens ist ein lesbarer String-Knoten (`HalleA.Oefen.OF-02.Planung.Zuordnungsregel` = „alle Ø 180“,
OF-01: „Ø 115 und Ø 125“). Er belegt, dass der Engpass OF-02 eine Planungsregel und keine technische Grenze ist.

**Betriebszustand** ist ein Enum-Datentyp (`Typen.Betriebszustand`, Wert = Index, Texte in `EnumStrings`): 0 Läuft, 1 Leerlauf,
2 Rüsten, 3 Störung, 4 WartungGeplant, 5 WartetMaterial, 6 WartetVorstufe, 7 LäuftOhneAuftrag. **Ausschussgrund** (je Takt):
0 Gut, 1 Gewicht, 2 Dicke, 3 Gewebe, 4 Etikett, 5 Bruch.

### Anlehnung an OPC 40001-1 (Machinery)

Das Modell orientiert sich pragmatisch an der Companion Spec Machinery, **ohne** deren Nodeset zu importieren:

- `Identitaet` entspricht dem Identification-Ordner (Hersteller, Baujahr, Kennung), `MachineryItemState` ist aus dem
  Betriebszustand abgeleitet (`Executing` / `NotExecuting` / `OutOfService`), Zähler und Betriebsstunden entsprechen den
  Betriebszähler-Variablen.
- Gründe gegen den Import: Das Sample soll mit dem vorhandenen SDK-Stand (1.5.378) ohne Nodeset-Abhängigkeit laufen; die
  Spezifikation verlangt eigene Typen mit deutschen Browse-Namen und Prozessgruppen, die Machinery nicht abdeckt; die
  Machinery-Typen (MachineryItemIdentification, MachineryItemState_StateMachine) würden den Collector komplizierter machen,
  ohne die Kernaussage der Demo zu verbessern.
- Der Hersteller ist fiktiv („Demo-Anlagenbau (fiktiv)“).

## Live-Werte

Eine Aktualisierungsschleife (Standard 1 s Realzeit) rechnet die Werte zur **Simulationsuhr** (`SimulationClock`, Start Demo-Uhr,
Geschwindigkeit konfigurierbar) und setzt nur geänderte Knoten (`ClearChangeMasks`, damit Subscriptions melden).

- Quelle: `LiveStateProvider` (Zustand, Zähler seit Schichtbeginn, Auftrag, Material, Form, Ofen, Puffer, Klima) und der Datensatzstrom
  des `HistoryGenerator` (letzter Takt, Minutenwerte, Störzähler, Alarme). Beim Start wird die Historie bis zur Startzeit eingespielt;
  jeder Schritt spielt `Generate(letzter Stand, jetzt)` ein. Die Drift an RT-04 wächst daher mit der Uhr weiter.
- **Zeitstempel** (SourceTimestamp) = Simulationszeit in UTC. Taktwerte der Rundtische tragen den Zeitstempel ihres Taktes und ändern
  sich mit dem Takt (ca. 4,2 s Simulationszeit), nicht mit dem Aktualisierungstakt.
- Fehlende Daten (noch kein Takt, Rundtisch ohne Form, kein Ablaufdatum) liefern Statuscode `BadNoData` statt einer Null.
- Modellannahmen ohne Quelle im Generator: Presszeit = 55 % der Taktzeit, Puffer-Kapazität 60 Platten, Türverriegelung „Verriegelt“,
  LetztePruefung ein fester Demo-Wert (maßgeblich für die Fälligkeit ist die Wartungs-DB), Störzähler und Betriebsstunden zählen seit
  Beginn der Historie (05.10.2026). Bei Verpackungen sind `SollMenge`/`IstMenge` die Schichtmengen in Kartons.
- „Platten je Durchmesser“ des Puffers ist nicht modelliert (der Generator kennt nur die Gesamtzahl).

## Historie (HistoryRead Raw)

Historisierte Variablen (`Historizing = true`, AccessLevel `HistoryRead`) liefern **Raw** (`ReadRawModifiedDetails`) für beliebige
Zeitfenster von **Mo 05.10.2026 00:00 bis zur Simulationszeit** exakt die Datensätze des `HistoryGenerator`.

| Quelle | Knoten | Zeitstempel |
|---|---|---|
| Takt (`CycleRecord`) | Rundtisch `Prozess.{Taktzeit, Presskraft, PresskraftAufbauzeit, Fuellgewicht, Dicke}` und `Zyklus.*` | Zeitpunkt des Takts |
| Minutenwerte (`MinuteValue`) | Zähler, Energie, Formtemperatur, Material, Hydraulik, Ofen, Puffer, Verpackung, Klima | Beginn der Minute; Zähler gelten seit Schichtbeginn |
| Zustandswechsel (`StateChange`) | `Zustand.Betriebszustand` | Zeitpunkt des Wechsels |
| Ereignisse | `Ereignisse.*` (siehe unten) | `TimestampUtc` des Datensatzes |

- Hinweis für Clients: `ReadRawModifiedDetails.IsReadModified` muss explizit `false` gesetzt werden (der Standardwert der SDK-Klasse ist
  `true`, der Server weist dann ab).
- Zeitfenster `[von, bis)`; fehlt die Endzeit, gilt die Simulationszeit; Zeiten davor und danach werden abgeschnitten.
- **Paging**: Je Knoten und Antwort höchstens `min(NumValuesPerNode, MaxValuesPerRead)` Werte (Standard 5000), danach ein
  **ContinuationPoint** (Guid, in der Sitzung abgelegt). Der Abruf der Folgeseite setzt exakt hinter dem letzten Wert auf.
  `ReleaseContinuationPoints` wird unterstützt.
- **Rückwärts lesen** (Endzeit vor Startzeit, oder nur Endzeit): die neuesten Werte zuerst.
- Nicht unterstützt: `IsReadModified`, `ReturnBounds` (ignoriert), `ReadProcessed`, `ReadAtTime`, `HistoryUpdate`. Die
  `HistoryServerCapabilities` sind nicht gesetzt.
- Der Generator arbeitet reproduzierbar mit festem Seed; das Lesen kostet nur Rechenzeit, Daten werden nicht vorgehalten.
  Minutenwerte und Ereignisse werden in 6-Stunden-Abschnitten erzeugt und, sobald sie vollständig in der Vergangenheit liegen,
  zwischengespeichert (höchstens 80 Abschnitte).
- **Messung** (Entwicklungsrechner, Verbindung ohne Verschlüsselung, im Prozess): alle **557.163 Taktzeiten** der vier Rundtische
  (`Prozess.Taktzeit`) in **113 Seiten à 5000 Werte in ca. 5 s**; allein RT-04 (124.566 Werte, 25 Seiten) in unter 1 s.

### Ereignisse: Variablen statt `HistoryReadEvents`

Verluste, Alarme und Auftragsereignisse sind über **historisierte Ereignis-Variablen** (Gruppe `Ereignisse` je Station) abrufbar, nicht
über `HistoryReadEvents`. Begründung:

- `HistoryReadEvents` verlangt eigene Event-Typen mit allen Feldern der Datensätze (Kategorie, Grund, Baugruppe, betroffene Stück …),
  Filterauswertung für beliebige Select-Clauses und eine Ereignis-Historie im Node Manager. Das SDK bietet dafür kein fertiges Gerüst;
  der Aufwand stünde in keinem Verhältnis zum Nutzen für das Sample.
- Die Variablen liefern **verlustfrei** die Domänen-Datensätze, mit demselben ContinuationPoint-Paging wie die Zykluswerte, und der
  Collector braucht nur HistoryRead Raw.
- Für das **Live**-Geschehen gibt es echte OPC-Ereignisse über Alarms & Conditions (unten).

Jede Variable ist ein String mit dem Datensatz als **JSON** (System.Text.Json, PascalCase, Enums als Text, ISO-8601). Gelesen werden
die Werte mit `HistoryJson.Deserialize<T>(json)` (Domain, Optionen in `HistoryJson.Options`); der Collector des Servers (`OpcHistorySource`) liest sie so. Ein Ereignis erscheint bei Beginn und bei Ende
als eigener Wert mit derselben `Id` (Verbraucher ersetzen den Stand je Id).

| Variable (`<Station>.Ereignisse.…`) | Datensatz | Stationen |
|---|---|---|
| `Verluste` | `LossEvent` | alle |
| `Alarmmeldungen` | `AlarmRecord` | alle |
| `Zustandswechsel` | `StateChange` (mit Auftrag) | alle |
| `Schichtplaene` | `ShiftPlanRecord` | alle |
| `Auftragsereignisse` | `OrderEvent` (Beginn, Menge erreicht, Ende) | Rundtische, Verpackungen |
| `Pressenlose` | `PressLot` (Rückverfolgung) | Rundtische |
| `Ofenchargen` | `OvenCharge` | Öfen |
| `Paletten` | `PalletRecord` | Verpackungen |
| `Minutenzaehlung` | `UnitCountRecord` (Kartons je Minute) | Verpackungen |

Der aktuelle Wert einer Ereignis-Variablen ist der zuletzt aufgetretene Datensatz (leerer String, wenn es noch keinen gab).

## Alarms & Conditions

- Jeder Alarm des Generators (`AlarmRecord`) ist eine **`AlarmConditionType`-Instanz** unter `<Station>.Alarme.<Alarm-Id>`.
  Beim Start entstehen die zur Startzeit aktiven; später angelegte Alarme und Wechsel kommen als Ereignisse.
- Felder: `SourceNode` = Station, `SourceName` = „Station / Baugruppe“ (z. B. „RT-04 / Hydraulik“), `ConditionName` = Alarm-Id, `Message`
  = Text, `ActiveState` (aktiv bis zum Ende laut Generator), `AckedState` (laut Generator, Störungen nach 2 min), `Retain` (nur aktive
  Alarme), `Time` = Zeitpunkt des Standes (Simulationszeit), `ConditionClassName` = Art (`Fault`, `ConditionWarning`, `ProcessWarning`,
  `Notice`).
- Severity: Hinweis 100, niedrig 300, mittel 500, hoch 800. ConditionClass: Störung → System, Zustandswarnung → Maintenance,
  Prozesswarnung und Hinweis → Process.
- Abonnieren am **Server-Objekt** (alle Stationen) oder an einer Station (alle Stationen sind Notifier, `HasNotifier` vom Server);
  `ConditionRefresh` liefert die aktiven Alarme. Wechsel (aktiv, quittiert, beendet) kommen als Ereignis mit Zeitstempel der Simulation.
- **Nur lesend**: Alle Methodenaufrufe im Namespace (Acknowledge, Confirm, AddComment, Enable, Disable, Suppress, RemoveFromService …)
  liefern `BadUserAccessDenied`. Das Quittieren in den Quelldaten kommt vom Generator, nicht von einem Client.

## Beobachtbarkeit

Der Simulator erzeugt Logs, Laufzeitmetriken und eigene Messwerte mit OpenTelemetry. Der Export per OTLP ist standardmäßig aus;
`Observability:OtlpEnabled=true` (oder `--Observability:OtlpEnabled=true`) sendet sie an `OTEL_EXPORTER_OTLP_ENDPOINT`
(Standard `http://localhost:4317`, ein beliebiger OTLP-Empfänger). Ressource
`service.name` = `opcagent-simulator`. Eigene Metriken: `simulator.history.reads` (HistoryRead-Aufrufe je Knoten) und
`simulator.history.values` (gelieferte Werte); dazu ein Span `simulator.history_read` je Dienstaufruf. Ohne Empfänger läuft er
unverändert. Übersicht: Abschnitt „Beobachtbarkeit“ im [README](../../README.md).

## Tests

`tests/OPCAgentSample.Tests/Simulator` startet den Simulator im Prozess (freier Port, Temp-PKI je Testlauf, Uhr angehalten) und prüft
mit einem eigenen OPC-Client: Namespace-Index, Browse-Struktur und Typen, Zuordnungsregel, Einheiten und Enums, Live-Werte gegen den
`LiveStateProvider` (RT-04 Presskraftaufbau ca. 1,1 s, Klima 23,4 °C), HistoryRead gegen die Generator-Datensätze (Anzahl, alle Werte,
Paging, rückwärts, Fehlerfälle), Alarme (`ConditionRefresh`, Felder), Abweisung von Acknowledge, Schreibzugriffen und Methoden,
Basic256Sha256 sowie die Geschwindigkeit 60× (Zeit rückt vor, Alarme entstehen und enden in Simulationszeit).

```powershell
dotnet test OPCAgentSample.slnx -c Release --filter "FullyQualifiedName~Tests.Simulator"
```
