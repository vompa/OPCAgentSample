# Slice 03 – Umschalten und Alt-Code entfernen

**Ziel:** Die Registrierung nutzt ChatClientModelClient, der alte Client ist weg, und ein Vergleichstest belegt die gleiche Anfrage an die API.
**Runde:** R01
**Quelle:** R01 – AC-01, AC-02; Aufgaben 6 und 7 der Anweisung
**Hängt ab von:** 02
**Modell:** sonnet
**Prüfstufe:** –
**IDs für neue Befunde:** F-13–F-18

## Umfang
- Zuerst Vergleichstest: JSON der alten Build-Anfrage als eingefrorener Sollwert gegen die Anfrage des echten Adapters über einen Stub-HttpMessageHandler, ohne Key; erlaubte Unterschiede nur laut Rulings
- Registrierung: AnthropicClient, AsIChatClient(options.Model, options.MaxTokens) als Singleton erst beim Auflösen; IAgentModelClient, IModelRequestDialect, IModelErrorMapper; Referenz-Engine ohne Key unverändert
- AnthropicModelClient und Build löschen
- ClaudeRequestTests, ModelReplyMappingTests, ModelStreamingTests umschreiben; Gegenüberstellung aller bisherigen Testfälle in der Rundenakte unter Surprises festhalten

## Muster übernehmen
- src/OPCAgentSample.Server/Agent/ClaudeRegistration.cs:21 – heutige Registrierung
- tests/OPCAgentSample.Tests/Agent/ModelStreamingTests.cs:13 – echte Streaming-Ereignisse

## Voraussichtlich geänderte Dateien
- src/OPCAgentSample.Server/Agent/ClaudeRegistration.cs
- src/OPCAgentSample.Server/Agent/ModelClient.cs
- src/OPCAgentSample.Server/Agent/AgentPrompt.cs
- tests/OPCAgentSample.Tests/Agent/
- README.md

## NICHT bauen
- Neue Konfigurationsschlüssel; Änderungen an MCP-Server, OPC-Anbindung oder Weboberfläche

## Prompt
```
Lies CLAUDE.md, work/conventions.md, die Rundenakte work/rounds/R01-*.md und diese Slice-Datei.
Setze Slice 03 "Umschalten und Alt-Code entfernen" um (diese Datei).
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
- [ ] Given Registrierung und Agent-Code When nach Anthropic-Typen gesucht wird Then finden sie sich nur in ClaudeRegistration.cs, AnthropicErrorMapper.cs und AnthropicRequestDialect.cs
- [ ] Given derselbe Verlauf When der Vergleichstest läuft Then weicht das JSON nur in den per Ruling erlaubten Punkten ab
- [ ] Given kein Key When der Server startet Then ist die Referenz-Engine aktiv wie bisher
- [ ] Given die volle Prüfkette When sie läuft Then ist sie grün, und jeder bisherige Testfall hat eine Entsprechung
- [ ] Manuell: Mit Key eine Frage in der Weboberfläche stellen: Karte erscheint, Überschrift streamt, LogUsage zeigt plausible Werte

## Nicht in dieser Slice
Neue Funktionen; Profile
