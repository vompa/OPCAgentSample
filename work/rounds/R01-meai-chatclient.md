Status: open

# Round R01 – meai-chatclient

## Source
`docs/arbeitsanweisung-meai.md` (Schritt 1 von 7). Die Anweisung gilt vollständig, soweit die Rulings unten sie nicht ändern. Anthropic 12.57.0 bleibt.

## Brief
Purpose: Der Agent ruft das Modell über `IChatClient` auf statt direkt über `MessageCreateParams`; Verhalten, Antwortkarten, Limits, Quota und Telemetrie bleiben gleich.
Users: Schichtleitung, Hallenleitung, Produktionsleitung (keine sichtbare Änderung).
- AC-01 Given die Engine When der Quelltext durchsucht wird Then enthält `ClaudeAnswerEngine.cs` kein `Anthropic.`
- AC-02 Given dieselbe Frage und derselbe Verlauf When die Anfrage über den echten Adapter gebaut wird Then entspricht ihr JSON der bisherigen `Build`-Anfrage, bis auf die unter Decisions genannten Unterschiede
- AC-03 Given alle Stopp-Gründe When die Engine sie auswertet Then entstehen dieselben Karten und derselbe Span-Tag `claude.stop_reason` wie vorher
- AC-04 Given Cache-Read und Cache-Write When die Nutzung abgebildet wird Then stimmen alle vier Token-Werte ohne Doppelzählung mit den bisherigen überein
- AC-05 Given Thinking und Redacted Thinking When sie im Rundlauf zurückgehen Then kommen sie als derselbe Blocktyp mit denselben Daten an
Edge cases: `Claude:Effort=max`; `pause_turn`; `model_context_window_exceeded`; ungültiges Tool-JSON; Assistant-Text mit Leerraum am Ende.
Errors: Jede `ModelFailureKind` wie bisher.
Non-goals: Siehe „Nicht in diesem Schritt“ der Anweisung.
NOT building: Weitere Anbieter, Profile, Kostenbudget in Geld, eigenes NuGet-Paket.
Assumptions: [Assumption] Der Smoke-Test mit echtem Key läuft erst nach der letzten Slice und nur, wenn ein Key eingerichtet ist.

## Slices
| Slice | Name | Findings / ACs | New ids |
|---|---|---|---|
| 01 | Neutrale Schnittstelle | AC-01, AC-03 | F-01–F-06 |
| 02 | ChatClient-Adapter | AC-03, AC-04, AC-05 | F-07–F-12 |
| 03 | Umschalten und Alt-Code entfernen | AC-01, AC-02 | F-13–F-18 |

## Decisions
Ruling: Die Plan-Empfehlungen gelten: Slices laufen nacheinander in frischen Sonnet-Sitzungen, Pläne werden einzeln freigegeben, Draft-PR nach der letzten Slice – User, 2026-11-02
Ruling: Effort geht als `OutputConfig.Effort` über `RawRepresentationFactory`, `ChatOptions.Reasoning` bleibt leer: `Reasoning` würde zusätzlich `thinking` senden und kennt kein `Max` – User, 2026-11-02
Ruling: Redacted Thinking geht über `RawRepresentation` als `RedactedThinkingBlockParam` zurück: der Marker des SDK ist `internal`, sonst würde daraus ein Thinking-Block – User, 2026-11-02
Ruling: `RawStopReason` kommt aus `ChatResponseUpdate.RawRepresentation`, damit `claude.stop_reason` gleich bleibt; `model_context_window_exceeded` wird zu `ModelStop.Other` und ergibt weiter die Karte `UnexpectedStop` – User, 2026-11-02
Ruling: Anthropic-Typen liegen nur in der Registrierung, in `AnthropicErrorMapper` und im neuen `AnthropicRequestDialect` (Schnittstelle `IModelRequestDialect`); `ChatClientModelClient` bleibt frei davon – User, 2026-11-02
Ruling: `RawRepresentationFactory` setzt `Model`, `MaxTokens` und `Messages = []` selbst aus `ClaudeOptions`, weil der Adapter `MaxOutputTokens` dann ignoriert – User, 2026-11-02
Ruling: Assistant-Text geht über `RawRepresentation` unverändert zurück, weil der Adapter sonst das Zeilenende kürzt und Leerraum-Blöcke verwirft – User, 2026-11-02
Ruling: Hingenommen und im PR zu nennen: Tool-Schemata bekommen durch den Adapter `additionalProperties: false` und seine Transformation; nur `properties` und `required` werden wie bisher übergeben – User, 2026-11-02
Ruling: `AllowMultipleToolCalls = true` wird gesetzt, damit kein `null`-Feld gesendet wird; parallele Tool-Aufrufe bleiben erlaubt – User, 2026-11-02
Build decision (slice 01): `ScriptedModelClient` baut jede `AgentModelRequest` mit `AnthropicModelClient.Build` zum API-JSON und legt es in `Requests` ab; die Anfrage-, Engine- und Endpoint-Tests bleiben dadurch inhaltlich unverändert – Claude, 2026-11-02
Build decision (slice 01): `ModelReply(Stop, RawStopReason, Blocks, Usage)`; `pause_turn`, `model_context_window_exceeded` und ein fehlender Grund werden `ModelStop.Other` (Karte `UnexpectedStop`, Log mit Rohwert oder „(keiner)“) – Claude, 2026-11-02
Build decision (slice 01): AC-01 ist als Scan-Test `Engine_source_names_no_Anthropic_namespace` festgehalten; er war im Gerüst-Commit schon grün (Basis hatte 3 Treffer), rot waren Stopp-Zuordnung und Auswertung des Enums – Claude, 2026-11-02
Build decision (slice 02): Der Dialekt hat statt `MarkCacheBreakpoint` die Methoden `CachedSystemText` und `ToolResult(item, cacheBreakpoint)` plus `IsRedactedThinking`; der Marker steckt im fertigen Block (`RawRepresentation`), ein nachträgliches Markieren entfällt, und `is_error` bleibt erhalten – Claude, 2026-11-02
Build decision (slice 02): Die Antwort wird im Client aus den Updates selbst zu Blöcken zusammengesetzt statt mit `ToChatResponse()`; nur so bleibt Redacted Thinking (leerer Text plus Signatur) von einem Thinking-Block unterscheidbar – Claude, 2026-11-02
Build decision (slice 02): `ToStop` ist im Dialekt eine Kopie der Zuordnung aus `AnthropicModelClient`; die Altkopie fällt in Slice 03 mit dem Löschen von `AnthropicModelClient` weg – Claude, 2026-11-02

## Surprises
- Slice 01: Im Worktree fehlt `src/OPCAgentSample.Server/wwwroot` (gebaute Weboberfläche, nicht eingecheckt): drei Tests (`WebApiTests` ×2, `DemoAccessEndpointTests`) waren dort rot, nach dem Kopieren des Ordners aus dem Hauptordner grün – Umgebung, kein Fehler der Slice (nicht in Git).
- Slice 01: `SnapshotTests.Checked_in_snapshot_matches_the_current_code` ist rot, auch einzeln und auf der unveränderten Basis `cf57031` im Hauptordner („machines_hall_lead.json … veraltet“) – vorbestehend, nicht Teil der Slice; die übrigen Tests der Gesamtsuite waren grün (626 von 630 im ersten Lauf, die drei Web-Fälle danach grün).
- Slice 02: Das SDK liefert `GetCacheControl` nur intern; der Cache-Marker ist deshalb am `RawRepresentation`-Block (`TextBlockParam`/`ToolResultBlockParam`) prüfbar, nicht über die Erweiterungsmethode.
- Slice 02: Alle 36 neuen Tests waren gegen das Gerüst (`NotImplementedException`) rot und nach der Umsetzung ohne Nacharbeit am Test grün. Volle Gesamtsuite lief in dieser Slice nicht (nur neue Dateien und ein Paket, keine gemeinsame Änderung); sie folgt mit der Registrierung in Slice 03.

## Deferred minors
- Slice 02 (Review): Fehlerabbildung ohne Testzeile für den `Other`-Zweig (`AnthropicException`-Basis) und ohne Prüfung der Meldungstexte – in Slice 03 nachziehen, wenn `AnthropicModelClient` entfällt.
- Slice 02 (Review): Über den echten Adapter sind `tools` und `tool_choice` (`disable_parallel_tool_use:false` durch `AllowMultipleToolCalls = true`, Schema-Transformation) nicht getestet; für AC-02 in Slice 03 beim JSON-Vergleich zuerst ansehen.
- Slice 02 (Review): Abbruch mitten im Stream und Weiterreichen des Tokens sind nicht getestet; zwei aufeinanderfolgende Denkblöcke ohne Zwischenblock würden zu einem (unwahrscheinlich, API sendet dazwischen Text oder Tool-Aufruf).
- Slice 02 (Review): Die Usage-Rechnung (Schlüssel `CacheCreationInputTokens`) liegt im neutralen Client; bei einem zweiten Anbieter gehört sie in den Dialekt.

## Open decisions

## Manual test list
### Slice 01 – Neutrale Schnittstelle
Start: `docker compose up -d`, `dotnet run --project src/OPCAgentSample.Simulator`, `dotnet run --project src/OPCAgentSample.Server` mit gesetztem `Claude:ApiKey`
1. In der Weboberfläche eine Frage stellen (z. B. „Wie ist die Lage?“).
Expected: Die Antwortkarte erscheint wie vor der Runde, die Überschrift streamt vorab, im Log steht „Claude-Frage: … Modellaufrufe …“ mit plausiblen Token-Werten.
Closes: AC-01, AC-03
Result: ☐

### Slice 02 – ChatClient-Adapter
Start: `dotnet test tests/OPCAgentSample.Tests -c Release --filter "FullyQualifiedName~ChatClientModelClientTests"`
1. Die Tests der Klasse laufen lassen (die Registrierung nutzt den neuen Client noch nicht, die Oberfläche ändert sich nicht).
Expected: alle 36 Tests sind grün.
Closes: AC-03, AC-04, AC-05
Result: ☐
