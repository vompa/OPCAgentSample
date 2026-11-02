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

## Surprises

## Deferred minors

## Open decisions

## Manual test list
