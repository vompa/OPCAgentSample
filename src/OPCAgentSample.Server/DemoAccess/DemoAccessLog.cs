namespace OPCAgentSample.Server.DemoAccess;

/// <summary>Protokoll der Anmeldungen: nur der Zugang (Name), nie der Code selbst.</summary>
internal static partial class DemoAccessLog
{
    public const string MainAccessName = "zugangscode";

    [LoggerMessage(Level = LogLevel.Information, Message = "Demo-Anmeldung über Zugang „{Access}“.")]
    public static partial void LoggedIn(ILogger logger, string access);
}
