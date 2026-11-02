using System.Text.RegularExpressions;

namespace OPCAgentSample.Tests.Agent;

/// <summary>AC-01: Anthropic-Typen gehören nur in die Registrierung, die Fehlerabbildung und den Anfrage-Dialekt.</summary>
public sealed partial class AnthropicTypesScanTests
{
    [Fact]
    public void Given_the_server_sources_When_searched_for_Anthropic_types_Then_only_three_files_name_them()
    {
        var server = Path.Combine(RepositoryRoot(), "src", "OPCAgentSample.Server");

        var files = Directory.EnumerateFiles(server, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                           && !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Where(path => AnthropicType().IsMatch(File.ReadAllText(path)))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["AnthropicErrorMapper.cs", "AnthropicRequestDialect.cs", "ClaudeRegistration.cs"], files);
    }

    [GeneratedRegex(@"using\s+Anthropic\b|\bAnthropic\.[A-Z]|\bAnthropicClient\b")]
    private static partial Regex AnthropicType();

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OPCAgentSample.slnx"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("OPCAgentSample.slnx nicht gefunden.");
    }
}
