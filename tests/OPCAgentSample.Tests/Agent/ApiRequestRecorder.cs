using System.Text;
using System.Text.Json;
using Anthropic;
using Microsoft.Extensions.AI;
using OPCAgentSample.Server.Agent;

namespace OPCAgentSample.Tests.Agent;

/// <summary>
/// Schickt Anfragen durch den echten Anthropic-Adapter (<c>AsIChatClient</c>) und zeichnet den HTTP-Body auf, den das SDK sendet.
/// Es gibt kein Netz und keinen Schlüssel: Ein <see cref="HttpMessageHandler"/> antwortet mit einem festen Stream.
/// </summary>
internal static class ApiRequestRecorder
{
    /// <summary>Ein kurzer Stream mit einem Textblock und dem angegebenen Stopp-Grund (JSON-Literal, z. B. <c>"end_turn"</c> oder <c>null</c>).</summary>
    public static string[] TextStream(string stopReasonJson) =>
    [
        """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-haiku-5-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":1}}}""",
        """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
        """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"{}"}}""",
        """{"type":"content_block_stop","index":0}""",
        """{"type":"message_delta","delta":{"stop_reason":STOP,"stop_sequence":null},"usage":{"output_tokens":2}}""".Replace("STOP", stopReasonJson, StringComparison.Ordinal),
        """{"type":"message_stop"}""",
    ];

    public static IChatClient RealAdapter(RecordingHandler handler, ClaudeOptions options)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(options);

        var anthropic = new AnthropicClient
        {
            ApiKey = "test-key",
            BaseUrl = "http://localhost",
            MaxRetries = 0,
            HttpClient = new HttpClient(handler),
        };
        return anthropic.AsIChatClient(options.Model, options.MaxTokens);
    }

    /// <summary>Die Anfrage so, wie sie mit dem echten Adapter als Body an die API ginge.</summary>
    public static async Task<JsonElement> RecordAsync(AgentModelRequest request, ClaudeOptions options)
    {
        var handler = new RecordingHandler(TextStream("\"end_turn\""));
        var client = new ChatClientModelClient(
            RealAdapter(handler, options),
            new AnthropicRequestDialect(Microsoft.Extensions.Options.Options.Create(options)),
            new AnthropicErrorMapper());
        await client.CreateAsync(request, null, CancellationToken.None);
        return JsonDocument.Parse(handler.Body!).RootElement.Clone();
    }

    /// <summary>Zeichnet den Body der Anfrage auf und antwortet mit einem festen SSE-Stream; es gibt kein Netz.</summary>
    public sealed class RecordingHandler(string[] events) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var sse = new StringBuilder();
            foreach (var json in events)
            {
                using var document = JsonDocument.Parse(json);
                sse.Append("event: ").Append(document.RootElement.GetProperty("type").GetString()).Append('\n');
                sse.Append("data: ").Append(json).Append("\n\n");
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(sse.ToString(), Encoding.UTF8, "text/event-stream"),
            };
        }
    }
}
