using OPCAgentSample.Server;

ServerHost.Build(new WebApplicationOptions { Args = args }).Run();

/// <summary>Einstiegspunkt; sichtbar für <c>WebApplicationFactory</c> in den Tests.</summary>
public partial class Program;
