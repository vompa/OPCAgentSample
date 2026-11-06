namespace OPCAgentSample.Tests.Agent;

/// <summary>Die Engine kennt keinen Anbieter: Anthropic-Typen gehören in Registrierung, Fehlerabbildung und Anfrage-Dialekt.</summary>
public sealed class ClaudeAnswerEngineSourceTests
{
    [Fact]
    public void Engine_source_names_no_Anthropic_namespace()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "OPCAgentSample.Server", "Agent", "ClaudeAnswerEngine.cs"));

        Assert.DoesNotContain("Anthropic.", source, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OPCAgentSample.slnx"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("OPCAgentSample.slnx nicht gefunden.");
    }
}
