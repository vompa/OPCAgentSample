using System.Text.Json;

namespace OPCAgentSample.Server.Demo;

/// <summary>Liest die eingebetteten Referenzdaten (<c>Demo/ReferenceData/*.json</c>, erzeugt aus dem Mockup).</summary>
internal static class ReferenceDataFiles
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    public static T Read<T>(string fileName)
    {
        var resource = $"Demo/ReferenceData/{fileName}";
        using var stream = typeof(ReferenceDataFiles).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Referenzdatei '{resource}' fehlt in der Assembly.");
        return JsonSerializer.Deserialize<T>(stream, s_json)
            ?? throw new InvalidOperationException($"Referenzdatei '{resource}' ist leer.");
    }
}
