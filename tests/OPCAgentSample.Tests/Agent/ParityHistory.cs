using System.Text.Json;
using OPCAgentSample.Server.Agent;

namespace OPCAgentSample.Tests.Agent;

/// <summary>Fester Verlauf für den Vergleich der Anfrage mit dem eingefrorenen Sollwert (<c>Reference/legacy-request.json</c>).</summary>
internal static class ParityHistory
{
    public static ClaudeOptions Options() => new() { Model = "claude-haiku-5-5", Effort = "medium", MaxTokens = 6000 };

    public static AgentModelRequest Request()
    {
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"scope":{"type":"string","description":"Bereich"}},"required":["scope"]}""").RootElement.Clone();
        AgentTool[] tools = [new("get_shift_summary", "Schichtbericht", schema), new("get_active_alarms", "Alarme", schema)];
        AgentTurn[] turns =
        [
            new UserQuestionTurn("Wie ist die Lage?"),
            new AssistantTurn(
            [
                new ModelThinking("Erst prüfen", "sig-1"),
                new ModelText("Ich schaue nach.   "),
                new ModelToolUse("t1", "get_shift_summary", JsonDocument.Parse("""{"scope":"A"}""").RootElement.Clone()),
                new ModelToolUse("t2", "get_active_alarms", JsonDocument.Parse("""{"scope":"A"}""").RootElement.Clone()),
            ]),
            new ToolResultsTurn([new ToolResultItem("t1", """{"wert":92}""", false), new ToolResultItem("t2", "Zugriff verweigert", true)]),
        ];
        return new AgentModelRequest(tools, turns, "owner-hash", true);
    }
}
