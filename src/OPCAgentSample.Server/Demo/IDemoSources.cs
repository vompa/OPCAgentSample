namespace OPCAgentSample.Server.Demo;

// Austauschbare Quellen der Fachinhalte. Die Referenz-Implementierungen (Referenz*) liefern Mockup-Daten;
// Kennzahlen-Engine, MCP-Server und Claude-Agent ersetzen sie gezielt (services.Replace).

/// <summary>Uhr der Demo. Die Referenz-Implementierung steht fest auf Do 15.10.2026 09:42 (Ortszeit Wien).</summary>
public interface IDemoClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>Anlagenübersicht: Halle mit Stationen und Klima, für den Produktionsleiter das Werk mit Hallen.</summary>
public interface IHallOverviewSource
{
    Task<HallOverviewSnapshot> GetAsync(string roleId, CancellationToken cancellationToken = default);
}

/// <summary>Beantwortet eine Frage: Tool-Aufrufe (für das MCP-Band) und danach die Antwortkarte.</summary>
public interface IAnswerEngine
{
    IAsyncEnumerable<AnswerStep> AskAsync(AskRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Vorbelegter Beispielverlauf je Rolle (erster Besuch, Spezifikation Abschnitt 16).</summary>
public interface ISampleHistorySource
{
    SampleTurn? GetSample(string roleId);
}

/// <summary>Postfach: Fälle aus den Eskalationsregeln. Der Besitzer trennt die Übernahme-Zustände (Live-Demo).</summary>
public interface IInboxSource
{
    Task<IReadOnlyList<InboxItem>> GetAsync(string roleId, string ownerKey, CancellationToken cancellationToken = default);

    /// <summary>Übernimmt einen Fall; ein bereits übernommener Fall bleibt unverändert. <see langword="null"/>: unbekannter Fall.</summary>
    Task<InboxItem?> TakeAsync(
        string roleId, string ownerKey, string itemId, string takenBy, string takenAtText,
        CancellationToken cancellationToken = default);
}

/// <summary>Ereignisstrom der Halle, ungefiltert und neueste zuerst; der Rollenfilter greift in der API.</summary>
public interface IFeedSource
{
    Task<IReadOnlyList<FeedEntry>> GetAllAsync(CancellationToken cancellationToken = default);
}

public interface IRoleProfileSource
{
    /// <summary>Rollen in Anzeigereihenfolge.</summary>
    IReadOnlyList<string> RoleIds { get; }

    RoleProfile? Find(string roleId);
}

public interface IPlantStatusSource
{
    Task<PlantStatus> GetAsync(CancellationToken cancellationToken = default);
}
