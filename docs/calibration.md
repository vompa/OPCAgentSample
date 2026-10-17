# Kalibrierung des Szenarios

Die Historie (Mo 05.10.2026 bis zur Demo-Uhr Do 15.10.2026 09:42) entsteht mit festem Seed im Projekt
`OPCAgentSample.Simulation`. Die Zahlen der Referenzantworten aus der Spezifikation (Abschnitt 6, 7 und 10) sind
Abnahmetests (`tests/OPCAgentSample.Tests/Simulation`). Das Szenario wurde so kalibriert, dass sie exakt bzw. in der
Rundung der Oberfläche herauskommen. Wo die Vorgaben sich widersprechen oder unterbestimmt sind, gilt die folgende
Auflösung.

## Kennzahl-Definitionen

| Kennzahl | Definition |
|---|---|
| Planbelegungszeit | Schichtzeit − Rüsten im Soll − geplante Wartung |
| Verfügbarkeit | Laufzeit / Planbelegungszeit (jeder ungeplante Stillstand außer Kurzstopps, auch Qualitätssperre) |
| Leistung | Σ(Stück × Soll-Takt) / Laufzeit |
| Qualität | Σ(Gut × Soll-Takt) / Σ(Stück × Soll-Takt) – zeitgewichtet, weil Kartons und Scheiben verschiedene Einheiten sind |
| OEE | Verfügbarkeit × Leistung × Qualität = Σ(Gut × Soll-Takt) / Planbelegungszeit; Hallenwerte aus Summen, nie aus gemittelten Prozenten |
| Hallen-OEE | Pressen und Verpackung; Öfen werden über die Belegung bewertet |
| Ist/Soll | (Gut + Ausschuss) / zeitanteilige Planmenge; Hochrechnung = Ist × 480 / verstrichene Minuten |
| Auftragsrückstand | linearer Planfortschritt − gefertigte Menge |
| Taktverlust (Stück) | Laufzeit × (1/Soll-Takt − 1/Ist-Takt) |
| MTBF / MTTR | Laufzeit / Störungen bzw. Störungsdauer / Störungen; Anteil ungeplant = Störung / (Störung + geplante Wartung) |
| Kosten | (ungeplanter Stillstand + Kurzstopps) × Kostensatz + Ausschuss × Stückkosten |
| Materialreichweite | Minimum aus Füllstand / Planverbrauch und „verarbeitbar bis“ |

Ampel (schlechtester Wert gewinnt): Ist/Soll < 90 % Warnung, < 75 % Problem (bei „wartet auf Vorstufe/Material“
höchstens Warnung); Überproduktion > 105 % ohne Auftrag Warnung; Takt > +5 % Warnung, > +15 % Problem;
Füllgewichts-Mittel ab ±1,0 g Warnung, ab ±2,0 g Problem; Form ab 90 % Warnung, ab 100 % Problem; Reichweite
< 60 min Warnung, < 15 min Problem; Ofen ab 95 % Warnung.

## Annahmen und aufgelöste Widersprüche

1. **RT-04 2.760 Stück bei Takt 4,6 s:** Mikrostopps unter 1 min sind als Nutzungsgrad (0,92) modelliert. „Takt 4,6 s“ ist der Mittelwert der letzten 5 min.
2. **Planmengen** je Presse: Früh/Spät 6.700, Nacht 5.800; Soll-Takt Ø 125 = 3,73 s (aus Pressen-OEE 79 %).
3. **Qualität zeitgewichtet** (stückbasiert ergäbe Hallen-OEE 80 statt 81 %).
4. **VP-02 64 % bei Verpackungs-OEE 84 %:** Tagesplan 900 Kartons liegt ca. 16 % über der Idealkapazität (37,1 s/Karton), Nachtplan 700.
5. **Verpackung, Öfen, Puffer sind nicht mengengekoppelt:** 1.240 Kartons à 25 Stück (ca. 31.000 Scheiben) passen nicht zu den Pressenmengen; Puffer folgt einem Profil, Öfen werden periodisch beladen.
6. **„5 von 8 Stationen“:** getestet ist 1 Problem + 5 Warnung (Mockup-Gruppen zählen 6).
7. **Ofen „Charge 3/5“:** Belegung = zeitgewichtete Platzbelegung seit Schichtbeginn; die Chargennummer wird nicht nachgebildet.
8. **Presskraftaufbau je Stunde:** lineare Drift ab 08:30; Stundenmittel 0,80/0,80/0,83/1,01 s, Endwert 1,1 s (Mockup: 0,80/0,82/0,95/1,10).
9. **Form F-125-07:** 94 % um 09:42, Grenze ca. 12:30, 97,8 % um 11:30 (Mockup-Werte sind mit konstantem Tempo nicht vereinbar).
10. **F-180-03 „4.100 Hübe/Tag“:** 4 Formnester je Rundtisch (Hub = Takt / 4); Prognose Grenze Mi 21.10.
11. **„Rückstand ca. 1.200“** = Auftragsrückstand 4711; **RT-04 „ca. 400“** = Taktverlust 08:30–14:00 (398 Stück).
12. **Ausschuss nach Qualitätsdrift:** nachts höher (6,4/7,6 %) als in der Früh (2,5 %); 24-h-Werte 2,8/3,1 %.
13. **Füllgewicht 112 g** gilt laut Mockup auch für Ø 180 (physikalisch vereinfacht).
14. **Kostensätze:** Stillstand RT-03 33,7 €/min, übrige Stationen 2,0–4,2 €/min – erzwungen durch „RT-03 9.800 €“ bei 291 min und „Halle A ca. 19.200 €“. Werk KW 42: 40,97 k€ (Ausschuss 14,5 k, Stillstand 26,5 k).
15. **Hallen B und C** sind verdichtete Wochenzähler je Schichtart, kalibriert auf den Hallen-A-Anteil (Werk-Leistung 93 % nur mit B/C bei 97–99 %). Ändert sich Halle A, müssen B/C neu kalibriert werden; die Tests melden das.
16. **„Mo 118 %“** = Nachtschicht Mo 12.10. an RT-02; Auftrag 4702 liegt auf RT-01.
17. **Nacht-OEE Mi:** größter Grund Mischung (138 min), dazu Personal fehlt 45 min und Sonstiges.
18. **„Störungen & Alarme“** zählt Störungen und Kurzstopps plus Alarme ohne eigenes Stillstandsereignis.
19. **Datenmenge:** ca. 0,56 Mio. Zykluswerte und 0,91 Mio. Minutenwerte bis zur Demo-Uhr (die Spezifikation schätzte 5 Mio.).
20. **Grundrauschen:** je Station und Schicht 2 Kurzstopps (zusammen 3 min); Do 06:00–09:42 ist durch das Drehbuch festgelegt.
21. **MTTR-Hallenschnitt** enthält Öfen und Verpackung; Wochenendwartung nur in der verdichteten Historie B.

## Nicht abgedeckt durch die Historie

Wartungs-DB-Antworten (Wartungsplan, Bündelung, KW 43, OF-01 überfällig), Postfach und Eskalation kommen aus der
Wartungs-DB bzw. den Regeln im Server; Rotations- und Bruchprüfung bestehen immer; Pausenmuster und Freitag-Auslauf
sind nicht modelliert.

## MCP

Der MCP-Server (`/mcp`, ModelContextProtocol.AspNetCore, Stateless HTTP) liefert 17 nur lesende Tools (die 15 der
Spezifikation plus `get_overproduction` und `trace_batch`), vier Resources und vier Prompts. Die Tests
(`tests/OPCAgentSample.Tests/Mcp`) rufen über den echten Endpunkt für jede Referenzfrage die genannten Tools mit den genannten
Parametern auf und prüfen die Zahlen der Kernaussage gegen die aufgenommene Historie zur Demo-Uhr. Kennzahlen kommen
ausschließlich aus `OPCAgentSample.Kpi`; die Tools lesen Blöcke (`KpiBlockService`, aus den verdichteten Zählern in Redis) und
Zeitreihen und geben je Ergebnis `source_until` (Datenstand) und `quelle` (Redis, Wartungsdaten, Auftragsdaten) aus.

### Absicherung und Rollen

| Thema | Umsetzung |
|---|---|
| Anmeldung | API-Key (`X-Api-Key` oder `Authorization: Bearer`), in `McpSecurity:ApiKeys` nur als SHA-256-Hash mit Name und Rolle (`shift_lead`, `hall_lead`, `production_lead`); Development-Keys in `appsettings.Development.json` (Klartext der Keys: `dev-shift-lead-key`, `dev-hall-lead-key`, `dev-production-lead-key`; den Hash erzeugt `ApiKeyHasher.Hash`, d. h. SHA-256 als Hex). Nur `/mcp` verlangt Anmeldung, die Web-API bleibt offen. Ein Eintrag mit ungültigem Hash verhindert den Start; ohne Key lehnt `/mcp` alles mit 401 ab. |
| Scope | Schichtleiter und Hallenleiterin: Halle A; Produktionsleiter: Werk (Hallen A bis C). Eine verbotene Anfrage (z. B. Werk als Schichtleiter) liefert „Keine Berechtigung …“ für das Modell und wird als `denied` auditiert. Wartungsplanung (`check_maintenance_conflicts`) nur Hallenleiterin und Produktionsleiter; `get_action_items` nur das eigene Postfach. |
| Meldungsfilter | Schichtleiter alle Meldungen, Hallenleiterin ab Warnung (keine Hinweise), Produktionsleiter nur Störungen (kritisch). |
| Audit | Jeder Tool-Aufruf: Client, Rolle, Tool, Argumente, `ok`, `denied` oder `error` im `AuditLog`. |
| Rate Limit | `McpSecurity:RateLimitPerMinute` (30) je Client, danach 429 mit `Retry-After`. |
| Größe | Listen sind auf `Mcp:MaxRows` (40) gekürzt, das Ergebnis vermerkt es unter `hinweise`. |

### Kalibrierung Wartungsplanung

Der Maschinenstillstand einer Wartung ist nicht ihre Arbeitsdauer (Spalte `durationMinutes` der Wartungs-DB): Die Formwartung
RT-02 dauert 40 min, die Presse steht dafür 25 min; die Türverriegelung OF-01 braucht 30 min Arbeit bei 20 min Stillstand; der
Umreifer VP-01 wird am Schichtwechsel gewartet (0 min zusätzlich). Diese Stillstandszeiten stehen in
`Mcp:Maintenance:MachineStopMinutes` (Standard ist die Arbeitsdauer). Wartungen mit dem Hinweis „bündelbar mit Rüsten“ bzw.
„Mischungswechsel“ kosten gebündelt 0 min. Damit ergeben die fünf Wartungen der nächsten 48 h exakt 95 min einzeln und 40 min
gebündelt (OF-01 20, RT-02 25, RT-04 20, RT-01 30, VP-01 0; gebündelt bleiben OF-01 und RT-04); KW 43 ergibt Mo 45, Di 60, Mi 0,
Do 30 min. Das Rüstfenster nach Auftragsende (`Mcp:Maintenance:SetupMinutes` 40) prüft, ob ein Termin hineinpasst; der
Auftragsende-Zeitpunkt ist Restmenge / Tempo der letzten 30 min (4698: 11:18, Referenz 11:20). Die Empfehlung „vorziehen“
entsteht, wenn eine Station eine offene Zustandswarnung an einer Baugruppe hat und deren reguläre Wartung später ansteht
(RT-04 Hydraulik, KW 45 → Mi 21.10. zusammen mit dem Formwechsel).

### Referenzantworten: Abdeckung

| Frage | Tools | Zahlen der Kernaussage (geprüft) |
|---|---|---|
| Schichtübergabe | `get_shift_summary` (vorherige), `get_active_alarms`, `get_order_progress`, `get_material_reach`, `get_maintenance_due` (Schicht) | Nacht 92 %; je Station 102/98/71/97/99/88 %; RT-03 kritisch; RT-01 Mischung 38 min (Verarbeitbarkeit bis 10:20); Form RT-02 94 %; OF-01 überfällig ✓ (Abweichung 1) |
| Hallenlage | `get_hall_overview`, `get_active_alarms`, `get_action_items` | 1 Problem (RT-03), 5 Warnung, 2 Läuft; Kette RT-04 → OF-02 → VP-02; 3 Aufgaben, 1 dringend ✓ (Abweichung 2) |
| Materialreichweite | `get_material_reach`, `get_order_progress` | RT-01 38 min (Füllstand 71 min); RT-02 Gewebe 55 min; VP-01 98 min; Charge M-2410-17 ✓ (Abweichung 3) |
| Schichtziel | `get_hall_overview`, `get_shift_summary` (aktuell), `get_order_progress` | Ist 11.150 / Soll 12.400; Hochrechnung 24.100 von 26.800 (90 %); RT-03 72 %, RT-04 89 %, Taktverlust RT-04 398 Stück ✓ (Abweichung 4) |
| Störungen & Alarme | `get_active_alarms` (seit 06:00), `get_downtime_pareto` | 4 Ereignisse, 68 min (RT-03 65, RT-02 2, VP-01 1), 1 offene Warnung RT-04 ✓ |
| Tagesbesprechung | `get_shift_summary` (gestern, alle), `get_downtime_pareto`, `check_maintenance_conflicts`, `get_material_reach` | OEE Früh 82 / Spät 80 / Nacht 72 %; Mischung 2,3 h ✓ |
| Hallen-OEE heute | `get_oee` (Bereich), `get_hall_overview`, `compare` | 81 %; Pressen 79, Verpackung 84, Öfen 87 % Belegung; Ausschuss 1,9 % ✓ |
| Zeitverluste | `get_downtime_pareto`, `compare` | Gewebezuführung 6,4 h, 14 Ereignisse, davon 9 an RT-03; Mischung 4,1 h; Ofen 3,5 h; Rüsten über Soll 3,2 h ✓ |
| Qualitätslage | `compare`, `get_process_trend`, `get_order_progress`, `get_maintenance_due` (Historie) | Ausschuss 24 h 2,8 / 3,1 / 1,2 / 0,9 %; Drift RT-01/RT-02 seit ca. 22:00 (+1,3 bis 1,5 g); Charge M-2410-17 an beiden; Waagen im Juli unauffällig; Hallenklima im Sollbereich ✓ |
| Wartungsplan | `get_maintenance_due` (48 h), `check_maintenance_conflicts`, `get_condition_warnings` | 5 Wartungen, OF-01 seit 2 Tagen überfällig; 95 → 40 min ✓ (siehe Kalibrierung) |
| Wochenbericht | `get_oee`, `compare` (Woche), `get_order_progress`, `get_reliability_kpis` | Planerfüllung 96 % (1,41 von 1,47 Mio. Scheiben); OEE 79 % (Vorwoche 78 %); Halle A/B/C 77/81/80 %; 4730 und 4733 kritisch ✓ |
| OEE-Vergleich | `get_oee` (Schicht), `get_downtime_pareto` | Nacht ist die schwächste Schicht, Ursache Verfügbarkeit; Warten auf Mischung Werk KW 42 5,8 h, Halle A Nacht 4,1 h; **73 % nicht reproduzierbar** (Abweichung 5) |
| Gefährdete Aufträge | `get_order_progress` (gefährdet), `get_oee`, `compare` | 4730 (58 %) und 4733 (41 %) kritisch, Termin Di 20.10. 14:00; 4711 (76 %) knapp, aufholbar; Halle B OEE 81 % ✓ (Abweichung 6) |
| Instandhaltung | `get_reliability_kpis` (Monat), `get_maintenance_due` (überfällig) | ungeplanter Anteil Jul 48, Aug 45, Sep 39, Okt 33 %; OF-01 Türverriegelung seit 2 Tagen ✓ (Abweichung 7) |
| Kosten der Woche | `get_downtime_pareto` (Kosten), `compare`, `get_reliability_kpis` | 40.971 € (Ausschuss 14.500, Stillstand 26.471); RT-03 9.807 €; Vorwoche 45.997 € (−11 %); Hallen 19,2 / 11,9 / 9,9 k€ ✓ |
| Warum steht VP-02? | `get_machine_status`, `get_hall_overview` (Ofen), `get_order_progress` (Stufe) | Wartet auf Vorstufe seit 09:05, 64 %; OF-02 96 % (alle Ø 180), OF-01 78 % (alle Ø 125); Kette RT-04 → OF-02 → VP-02 ✓ (Abweichung 8) |
| Öfen Engpass? | `get_hall_overview`, `get_process_trend`, `get_order_progress` | OF-02 96 %, OF-01 78 %; Puffer 42 Platten, Ø 45 min; Temperaturprofil gleich ✓ |
| Wartungen nächste KW | `get_maintenance_due` (von/bis, Prognose), `get_condition_warnings`, `check_maintenance_conflicts` | KW 43: 4 Wartungen (2 fest, 2 Prognose); Empfehlung Hydraulik RT-04 vorziehen auf Mi 21.10. 13:00; Stillstand je Tag 45/60/0/30 ✓ |
| RT-03 überholen? | `get_reliability_kpis`, `get_downtime_pareto` (Baugruppe), `get_maintenance_due` | MTBF 31 h gegenüber 78 h; MTTR 42 min, Hallenschnitt 38 min; 78 % der Störungen Gewebezuführung ✓ |
| RT-04 hängt hinterher | `get_machine_status`, `get_process_trend`, `get_condition_warnings`, `get_maintenance_due` | Takt 4,6 s (Soll 4,2), Presskraftaufbau 1,1 s (Soll 0,8), 0 min Stillstand; Drift seit 08:30; Verlust bis Schichtende 398 Stück ✓ |
| Überproduktion | `get_overproduction` | RT-01 +2.050 (Di 06.10.), RT-02 +3.600 (Mo-Nacht, 118 %, im Dashboard grün); Puffer 58 Platten; Wartezeiten RT-04 70 min, VP-02 45 min ✓ |
| Charge M-2410-17 | `trace_batch` | 2 Pressenlose (RT-01, RT-02), 7 Ofenchargen, 17 Paletten, 2.040 Kartons, keiner ausgeliefert, Prüfungen bestanden ✓ |
| Weihnachtsstillstand | Prompt `weihnachtsstillstand`, `get_maintenance_due` (von/bis) | Wartungsfenster 24.12.–06.01.: zwei Ofenrevisionen und vier Generalinspektionen ✓ |

### Abweichungen von den Referenzantworten

1. **Schichtübergabe:** Das Mockup zeigt den Stand 06:04; zur Demo-Uhr 09:42 ist die Störung RT-03 (05:12–07:05) beendet, die Mischung RT-01 ist „verarbeitbar bis 10:20“ (nicht 07:20) und die Standzeitgrenze RT-02 liegt bei ca. 12:31 (Referenz 12:40, hochgerechnet aus dem Hubtempo der letzten Stunde). Der Verschleiß der Form zum Rüsten um 11:30 beträgt 98 % (Referenz 97 %).
2. **Hallenlage:** Die Kernaussage „5 von 8“ zählt die Warnungen ohne das Problem; das Tool nennt 1 Problem und 5 Warnungen (Annahme 6).
3. **Materialreichweite:** Die Reichweite rechnet mit dem beobachteten Verbrauch der letzten 30 min, nicht mit dem Planverbrauch: RT-04 118 min (begrenzt durch die Verarbeitbarkeit der Mischung A30 bis 11:40; Referenz 150), VP-01 98 min (Referenz 100), VP-02 ohne Verbrauch, solange sie wartet (Referenz 170).
4. **Rückstand RT-03 „ca. 1.200“:** Der Auftragsrückstand braucht das geplante Auftragsende (Szenario: 15.10. 21:01), das nicht in der Datenbank steht. `get_shift_summary` nennt die Lücke zum Schichtplan (RT-03: 72 %, Hochrechnung 4.832 von 6.700), `get_order_progress` den Termin (Fr 16.10. 22:00, 36 h verfügbar, 10 h nötig: aufholbar) und die Bemerkung der Auftragsdaten mit den 1.200 Stück. Der Verlust von RT-04 (398 Stück) kommt aus der Drift und der Kennzahlen-Bibliothek.
5. **OEE-Vergleich 73 / 82 / 81 %:** Die Hallen B und C liegen in Redis nur als Tageszähler vor (Historie B), nicht je Schichtart. Der Schichtvergleich gilt daher für Halle A (Früh 78, Spät 78, Nacht 75 %; Verfügbarkeit 95 / 98 / 93 %, Nacht ist die schwächste) und das Ergebnis weist darauf hin. Wochenwerte des Werks (OEE 79 %, Mischung 5,8 h) stimmen, weil sie aus Tagessummen entstehen.
6. **Gefährdete Aufträge:** Aufträge ohne Station (Halle B) teilen sich die Hallenkapazität: Bei dem bisherigen Tempo (184 und 174 Stück/h) brauchen beide zusammen 129 h, bis Di 20.10. 14:00 stehen 76 h zur Verfügung, die Kapazität reicht nur für einen. „Übrige 14 Aufträge im Plan“ gibt es nicht: Die Auftragsdaten enthalten 8 offene Aufträge (5 im Plan).
7. **Instandhaltung:** „Wartungsplantreue 92 %“ und „18 gebündelte Wartungen im Oktober“ stehen nicht in der Wartungs-DB (nur Pläne und erledigte Waagen-Kalibrierungen) und werden nicht ausgegeben. Der ungeplante Anteil rechnet mit abgeschlossenen Produktionstagen (ohne den laufenden Tag).
8. **Ofen-Freigabe:** „Freigabe 10:15“ und „freie Etage ab 09:50“ sind Knoten des OPC-Servers; die Historie enthält Ofenchargen erst nach der Entladung. Die Zuordnungsregel („alle Ø 180“) leitet das Tool aus den Chargen der letzten 24 h ab.
9. **Postfach:** Die Fälle entstehen aus Regeln (siehe `Demo/RuleInboxSource.cs`) und nicht mehr aus dem Mockup. Die Titel entsprechen dem Mockup (z. B. „Rückstand RT-03: Umverteilung entscheiden“, „Mischung A60 für RT-01 anfordern“, Frist bis 09:50 = Ende der Verarbeitbarkeit minus 30 min Vorlauf); Details nennen berechnete Werte (RT-03: ca. 864 Stück unter Plan statt 1.200, Kapazität bei RT-02 ab 11:18 statt RT-01 ab 11:30).
