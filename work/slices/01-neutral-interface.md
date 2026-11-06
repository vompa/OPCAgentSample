# Slice 01 – Neutrale Schnittstelle

**Ziel:** Die Engine hängt nur noch an IAgentModelClient und ModelStop; das Verhalten bleibt gleich, der bisherige Anthropic-Aufruf läuft hinter der neuen Schnittstelle.
**Runde:** R01
**Quelle:** R01 – AC-01, AC-03; Aufgaben 2, 3 und 5 der Anweisung
**Hängt ab von:** –
**Modell:** sonnet
**Prüfstufe:** –
**IDs für neue Befunde:** F-01–F-06

## Umfang
- IAgentModelClient mit CreateAsync(AgentModelRequest, Action<string>?, CancellationToken) und AgentModelRequest(Tools, Turns, OwnerHash, AllowTools) ersetzen IClaudeModelClient
- ModelReply bekommt ModelStop { Done, ToolCalls, MaxTokens, Refused, Other } und RawStopReason; Conclude nutzt das Enum, claude.stop_reason und LogStop nutzen RawStopReason
- ClaudeRequest wird zu AgentPrompt mit SystemPrompt, QuestionText, OwnerHash; Build wandert vorerst nach AnthropicModelClient, das IAgentModelClient implementiert
- AgentTestSupport und die Engine-Tests auf IAgentModelClient umstellen, inhaltlich unverändert

## Muster übernehmen
- src/OPCAgentSample.Server/Agent/ClaudeAnswerEngine.cs:181 – Conclude, das Verhalten je Stopp-Grund
- src/OPCAgentSample.Server/Agent/ModelClient.cs:60 – bisheriger Client

## Voraussichtlich geänderte Dateien
- src/OPCAgentSample.Server/Agent/ModelClient.cs
- src/OPCAgentSample.Server/Agent/ClaudeRequest.cs
- src/OPCAgentSample.Server/Agent/AgentPrompt.cs
- src/OPCAgentSample.Server/Agent/ClaudeAnswerEngine.cs
- src/OPCAgentSample.Server/Agent/ClaudeRegistration.cs
- tests/OPCAgentSample.Tests/Agent/

## NICHT bauen
- IChatClient, neue Pakete, Löschen von AnthropicModelClient

## Prompt
```
Lies CLAUDE.md, work/conventions.md, die Rundenakte work/rounds/R01-*.md und diese Slice-Datei.
Setze Slice 01 "Neutrale Schnittstelle" um (diese Datei).
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
- [ ] Given ClaudeAnswerEngine.cs When nach Anthropic. gesucht wird Then gibt es keinen Treffer
- [ ] Given end_turn oder stop_sequence When Conclude läuft Then gilt ModelStop.Done und die Karte entsteht wie bisher
- [ ] Given refusal, max_tokens, pause_turn oder keinen Stopp-Grund When Conclude läuft Then entstehen Refused, TooLongAnswer bzw. UnexpectedStop wie bisher und claude.stop_reason trägt den Rohwert
- [ ] Given alle Agent-Tests When sie laufen Then sind sie grün, ohne dass ein Testinhalt abgeschwächt wurde

## Nicht in dieser Slice
Anbieterwechsel; Änderungen an Karten, Limits, Quota oder Konfiguration
