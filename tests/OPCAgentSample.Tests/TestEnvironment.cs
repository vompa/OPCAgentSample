// UseEnvironment(...) für alle Testserver-Fabriken
global using Microsoft.AspNetCore.Hosting;

namespace OPCAgentSample.Tests;

/// <summary>
/// Umgebung aller Testserver. <c>WebApplicationFactory</c> startet sonst in <c>Development</c>, und dort lädt .NET die
/// User-Secrets des Servers – Tests würden dann einen echten Claude-Key sehen und echte, kostenpflichtige Aufrufe machen.
/// </summary>
public static class TestEnvironment
{
    public const string Name = "Testing";
}
