# Arbeitsanweisung: Agent auf Microsoft.Extensions.AI (`IChatClient`) umbauen

Schritt 1 von 7 des Plans „anbieterunabhängiger Agent“. Ziel: Der Agent ruft das Modell nicht mehr direkt über das Anthropic-SDK
(`MessageCreateParams`) auf, sondern über `IChatClient`. **Verhalten, Antwortkarten, Limits, Quota und Telemetrie bleiben gleich;
es läuft weiterhin nur Claude.** OpenAI/Azure OpenAI und Profile sind ausdrücklich *nicht* Teil dieses Schritts.

## Ausgangslage (Stand `main`, e1ba751)

Alles in `src/OPCAgentSample.Server/Agent/`:

- `ClaudeAnswerEngine` – eigene Tool-Schleife (Runden-/Tool-Limits, Wrap-up-Runde ohne Tools, Streaming der Kartenüberschrift über
  `AnswerDraftExtractor`, Spans `claude.model_call`, `AppTelemetry.Tokens`). **Bleibt die Schleife, wird nur umverdrahtet.**
- `ModelClient.cs` – neutrale Typen `ModelBlock`/`ModelText`/`ModelThinking`/`ModelRedactedThinking`/`ModelToolUse`, `ModelReply`,
  `ModelUsage`, `ModelFailureKind`, `ModelCallException`; dazu `IClaudeModelClient` mit
  `CreateAsync(MessageCreateParams, …)` (**die Kopplung**) und `AnthropicModelClient` (Fehler-Mapping auf `ModelFailureKind`).
- `ClaudeRequest.cs` – Systemprompt, `QuestionText`, `OwnerHash`, und `Build(...)` → `MessageCreateParams` mit: Tools nach Name
  sortiert, Cache-Breakpoint am Systemprompt und am letzten Tool-Ergebnis, `ToolChoiceAuto/None`, `OutputConfig.Effort`,
  JSON-Schema-Format der Antwortkarte (`AnswerCardFormat.Schema()`), `Metadata.UserID = ownerHash`.
- `ClaudeRegistration.cs` – registriert `IClaudeModelClient` → `AnthropicModelClient`.
- Tests: `tests/OPCAgentSample.Tests/Agent/` (`ClaudeRequestTests`, `ModelReplyMappingTests`, `ModelStreamingTests`,
  `ClaudeAnswerEngineTests`, `AgentTestSupport` mit skriptiertem Client).

## Fakten zum Anthropic-Adapter (geprüft im SDK-Quellcode, `AnthropicClientExtensions.cs`)

- `anthropicClient.AsIChatClient(modelId, maxOutputTokens, AnthropicThinkingMode.Adaptive)` liefert den `IChatClient`.
- Thinking: kommt als `TextReasoningContent`, Signatur in `ProtectedData`; Redacted Thinking als `TextReasoningContent("")` mit
  `ProtectedData = data`. Wird beim Zurückschicken korrekt wieder zu Thinking-Blöcken.
- Prompt-Caching: `content.WithCacheControl(new CacheControlEphemeral())` (Namespace `Microsoft.Extensions.AI`, Klasse
  `AIContentCacheExtensions`) auf `TextContent`/`FunctionResultContent`; bei Tools über
  `AdditionalProperties[nameof(Tool.CacheControl)]`.
- Effort: `ChatOptions.Reasoning.Effort` → `output_config.effort`.
- Antwortkarte: `ChatOptions.ResponseFormat = ChatResponseFormat.ForJsonSchema(...)` wird als JSON-Schema-Output gesendet.
- Stopp-Grund: `FinishReason` – `end_turn`/`stop_sequence` → `Stop`, `max_tokens` → `Length`, `tool_use` → `ToolCalls`,
  `refusal` → `ContentFilter`, Unbekanntes verbatim in `FinishReason.Value`.
- Alles andere (z. B. `metadata.user_id`): `ChatOptions.RawRepresentationFactory` gibt ein vorbefülltes `MessageCreateParams`
  zurück; der Adapter überschreibt gesetzte Werte nicht.
- **Usage-Falle:** `InputTokenCount` = Input **+ Cache-Read + Cache-Write**; `CachedInputTokenCount` = Cache-Read;
  Cache-Write in `AdditionalCounts["CacheCreationInputTokens"]`. Das heutige `ModelUsage.InputTokens` ist nur der ungecachte Input.
- Exceptions des SDK werden unverändert durchgereicht.

Diese Punkte gegen die tatsächlich eingebundene Paketversion verifizieren (ggf. `Anthropic` auf eine Version mit diesen APIs
anheben und das im PR begründen).

## Aufgaben

1. **Pakete**: `Microsoft.Extensions.AI` (bzw. `.Abstractions`) im Server-Projekt ergänzen. Keine weiteren Anbieter-Pakete.
2. **Neutrale Anfrage**: `IClaudeModelClient` ersetzen durch ein anbieterneutrales `IAgentModelClient`:
   ```csharp
   Task<ModelReply> CreateAsync(AgentModelRequest request, Action<string>? onTextDelta, CancellationToken ct);
   public sealed record AgentModelRequest(IReadOnlyList<AgentTool> Tools, IReadOnlyList<AgentTurn> Turns,
       string OwnerHash, bool AllowTools);
   ```
   In der Engine darf danach **kein** `Anthropic.*`-Typ mehr vorkommen (auch nicht in `CallModel*Async`).
3. **Stopp-Grund als Enum**: `ModelReply.StopReason` → `ModelStop { Done, ToolCalls, MaxTokens, Refused, Other }` plus
   `RawStopReason` (string, für Logs). `Conclude` auf das Enum umstellen, Verhalten identisch (inkl. `stop_sequence` → `Done`).
4. **`ChatClientModelClient : IAgentModelClient`** (neue Datei) auf Basis eines `IChatClient`:
   - `AgentTurn` → `ChatMessage`: System-Prompt als `ChatRole.System` mit `WithCacheControl`; `UserQuestionTurn` → User-Text;
     `AssistantTurn` → `TextContent` / `TextReasoningContent` (Signatur in `ProtectedData`) / `FunctionCallContent`;
     `ToolResultsTurn` → **eine** Nachricht mit allen `FunctionResultContent` in Reihenfolge, Cache-Marker am letzten Ergebnis
     des letzten ToolResults-Turns (wie heute).
   - Tools: `AgentTool` → `AIFunctionDeclaration` mit dem MCP-JSON-Schema (`AIFunctionFactory.CreateDeclaration(name, description,
     schema)` o. ä.), **nach Name sortiert**. Nicht ausführbar – die Engine ruft die Tools selbst über `IAgentToolConnection`.
   - `ChatOptions`: `ToolMode = AllowTools ? Auto : None`, `Reasoning.Effort` aus `ClaudeOptions.Effort`, `MaxOutputTokens`,
     `ResponseFormat` aus `AnswerCardFormat.Schema()`, `RawRepresentationFactory` für `Metadata.UserID = OwnerHash`.
   - Streaming: `GetStreamingResponseAsync`, Text-Deltas (nur `TextContent`, kein Reasoning) an `onTextDelta`,
     danach `ToChatResponse()` → `ModelReply`.
   - **Usage-Mapping** auf `ModelUsage` so, dass die Werte exakt den heutigen entsprechen:
     `Input = InputTokenCount − CachedInputTokenCount − CacheCreationInputTokens`, `CacheRead = CachedInputTokenCount`,
     `CacheWrite = AdditionalCounts["CacheCreationInputTokens"]`, `Output = OutputTokenCount`. Fehlende Werte = 0.
   - Fehler-Mapping: in eine eigene Klasse `AnthropicErrorMapper` (heutige Logik aus `AnthropicModelClient`) auslagern; der
     Client fängt Exceptions und übersetzt über ein Interface `IModelErrorMapper` → `ModelCallException`.
5. **Prompt-Inhalte bleiben in `ClaudeRequest`** (umbenennen in `AgentPrompt` ist ok): `SystemPrompt`, `QuestionText`, `OwnerHash`.
   `Build(...)` und alle Anthropic-Typen dort entfernen.
6. **Registrierung**: `AnthropicClient { ApiKey }` → `.AsIChatClient(options.Model, options.MaxTokens)` als Singleton
   `IChatClient`, darauf `IAgentModelClient` → `ChatClientModelClient`, `IModelErrorMapper` → `AnthropicErrorMapper`.
   Konfiguration (`Claude:*`) bleibt unverändert; Referenz-Engine ohne Key bleibt wie gehabt.
7. **`AnthropicModelClient` und `IClaudeModelClient` löschen**, sobald alles grün ist.

## Tests

- `AgentTestSupport`: skriptierten Client auf `IAgentModelClient` umstellen (Engine-Tests sollen inhaltlich unverändert bleiben).
- Neu `ChatClientModelClientTests` mit einem Fake-`IChatClient`, der die gesendeten `ChatMessage`s/`ChatOptions` aufzeichnet und
  skriptierte `ChatResponseUpdate`s liefert. Abzudecken:
  - Reihenfolge/Sortierung der Tools, System-Prompt mit Cache-Marker, Cache-Marker nur am letzten Tool-Ergebnis
  - Thinking-Signatur und Redacted Thinking im Rundlauf (Antwort → `AssistantTurn` → nächste Anfrage)
  - `AllowTools = false` → `ToolMode.None`; Effort und ResponseFormat gesetzt; `RawRepresentationFactory` setzt `UserID`
  - alle Stopp-Gründe inkl. unbekanntem Wert → `ModelStop.Other`
  - Usage-Mapping mit Cache-Read und Cache-Write (keine Doppelzählung)
  - Text-Deltas: nur Antworttext, kein Reasoning
  - Fehler-Mapping je `ModelFailureKind`
- `ClaudeRequestTests`, `ModelReplyMappingTests`, `ModelStreamingTests`: auf die neue Abbildung umschreiben oder durch die neuen
  Tests ersetzen – **keine Abdeckung verlieren** (vorher die Testfälle auflisten und im PR gegenüberstellen).
- Optional, wenn `Claude__ApiKey` lokal gesetzt ist: ein manueller Smoke-Test (eine Frage in der Weboberfläche) und Vergleich der
  geloggten Usage (`LogUsage`) mit einem Lauf auf `main`.

## Abnahmekriterien

- `dotnet build OPCAgentSample.slnx -c Release` ohne neue Warnungen, `dotnet test` grün, `npm run build`/`npm run test` grün.
- `grep -r "Anthropic\." src/OPCAgentSample.Server/Agent/ClaudeAnswerEngine.cs` findet nichts; Anthropic-Typen nur noch in der
  Registrierung und in `AnthropicErrorMapper`.
- Gleiche Antwortkarten, gleiche Limits, gleiche Span-Tags und Token-Metriken wie vorher.
- Keine Änderung an MCP-Server, OPC-Anbindung, Web-Oberfläche oder Konfigurationsschema.

## Arbeitsweise

- Eigener Branch `feature/meai-chatclient`, kleine Commits (Interface → Adapter → Tests → Aufräumen).
- Erst den Plan mit betroffenen Dateien kurz zusammenfassen und bestätigen lassen, dann umsetzen.
- Bei Abweichungen der SDK-API von den „Fakten“ oben: stoppen, die Abweichung zeigen, Lösungsvorschlag machen.
- Am Ende Draft-PR gegen `main` mit Zusammenfassung, Testgegenüberstellung und offenen Punkten.

## Nicht in diesem Schritt

Profile pro Endpoint, Kostenbudget in Geld, Azure OpenAI/OpenAI/Bedrock/lokale Modelle, eigenes NuGet `Vompa.Agents.*`.
