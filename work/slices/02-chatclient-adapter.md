# Slice 02 – ChatClient-Adapter

**Ziel:** ChatClientModelClient bildet AgentModelRequest und Modellantworten vollständig auf einen IChatClient ab, noch ohne dass die Registrierung ihn nutzt.
**Runde:** R01
**Quelle:** R01 – AC-03, AC-04, AC-05; Aufgabe 1 und 4 der Anweisung sowie die Rulings
**Hängt ab von:** 01
**Modell:** sonnet
**Prüfstufe:** –
**IDs für neue Befunde:** F-07–F-12

## Umfang
- Direkte Paketreferenz Microsoft.Extensions.AI.Abstractions in der aufgelösten Version 10.8.3; das Paket Microsoft.Extensions.AI ist nicht nötig
- ChatClientModelClient: Turns zu ChatMessage, Tools nach Name sortiert über AIFunctionFactory.CreateDeclaration, ToolMode, ResponseFormat, Streaming, Text-Deltas nur aus nicht leerem Antworttext
- IModelRequestDialect mit AnthropicRequestDialect: Cache-Marker, Redacted Thinking, unveränderter Assistant-Text, Optionen über RawRepresentationFactory (Model, MaxTokens, Messages = [], Effort einschließlich max, Metadata.UserID), Rohwert des Stopp-Grunds
- IModelErrorMapper mit AnthropicErrorMapper aus der heutigen Logik; OperationCanceledException bleibt unberührt
- Usage: Input = InputTokenCount − CachedInputTokenCount − CacheCreationInputTokens, fehlende Werte 0
- Ungültiges Tool-JSON (FunctionCallContent.Exception) ergibt ein Nicht-Objekt als Input, damit die Engine ihren bestehenden Fehlertext sendet
- ChatClientModelClientTests mit Fake-IChatClient nach der Testliste der Anweisung

## Muster übernehmen
- src/OPCAgentSample.Server/Agent/ModelClient.cs:70 – Fehlerabbildung
- src/OPCAgentSample.Server/Agent/ClaudeRequest.cs:126 – Abbildung der Turns und Cache-Marker

## Voraussichtlich geänderte Dateien
- src/OPCAgentSample.Server/Agent/ChatClientModelClient.cs
- src/OPCAgentSample.Server/Agent/AnthropicRequestDialect.cs
- src/OPCAgentSample.Server/Agent/AnthropicErrorMapper.cs
- src/OPCAgentSample.Server/OPCAgentSample.Server.csproj
- tests/OPCAgentSample.Tests/Agent/ChatClientModelClientTests.cs

## NICHT bauen
- Umschalten der Registrierung, Löschen von Alt-Code

## Prompt
```
Lies CLAUDE.md, work/conventions.md, die Rundenakte work/rounds/R01-*.md und diese Slice-Datei.
Setze Slice 02 "ChatClient-Adapter" um (diese Datei).
1. Stelle zuerst einen Plan vor (Dateien, Klassen, Tests) mit einem Abschnitt
   „Annahmen (bei Freigabe bestätigen)“ für jeden offenen Punkt und einem kurzen „Review-Fokus“,
   speichere ihn unter work/plans/ und warte auf Freigabe.
2. Schreibe die Abnahmetests dieser Slice als fehlschlagende Tests (ROT).
3. Implementiere das Minimum bis grün. Halte jede Regel in CLAUDE.md und work/conventions.md ein.
4. Prüfe die Definition of Done. Halte Entscheidungen, Überraschungen und zurückgestellte
   Kleinigkeiten in der Rundenakte und in der Protokolldatei fest, die work/conventions.md nennt.
```

## Definition of Done
- [ ] Prüfungen aus work/conventions.md grün
- [ ] Kein Test abgeschwächt oder übersprungen
- [ ] Keine neuen Pakete ohne Freigabe
- [ ] Entscheidungen festgehalten
- [ ] Given mehrere Tool-Ergebnisse When die Anfrage entsteht Then stehen sie in einer Nachricht und nur das letzte des letzten ToolResultsTurn trägt den Cache-Marker
- [ ] Given ModelThinking und ModelRedactedThinking When sie über den echten Adapter gehen Then kommen thinking mit Signatur und redacted_thinking mit denselben Daten an
- [ ] Given Claude:Effort=max When die Optionen entstehen Then ist OutputConfig.Effort Max und ChatOptions.Reasoning null
- [ ] Given Input 100, Cache-Read 30, Cache-Write 20, Output 7 als Adapterwerte When die Nutzung abgebildet wird Then ergibt sich 50, 7, 30, 20
- [ ] Given jede Fehlerart des SDK When sie im Stream auftritt Then entsteht dieselbe ModelFailureKind wie bisher
- [ ] Given Reasoning-Text und Antworttext im Stream When sie eintreffen Then erreicht onTextDelta nur der Antworttext

## Nicht in dieser Slice
OpenAI, Azure OpenAI oder andere Anbieter; Prompt-Änderungen
