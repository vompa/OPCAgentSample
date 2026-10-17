using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OPCAgentSample.Domain;

namespace OPCAgentSample.Server.MasterData;

/// <summary>Liest die eingebetteten Seed-Dateien (<c>Seed/*.json</c>).</summary>
internal static class SeedFiles
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static T Read<T>(string fileName)
    {
        var resource = $"Seed/{fileName}";
        using var stream = typeof(SeedFiles).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Seed-Datei '{resource}' fehlt in der Assembly.");
        return JsonSerializer.Deserialize<T>(stream, s_json)
            ?? throw new InvalidOperationException($"Seed-Datei '{resource}' ist leer.");
    }
}

/// <summary>
/// Löst Zeitangaben der Seed-Dateien relativ zur Demo-Uhr auf: <c>today</c>, <c>tomorrow</c>, <c>yesterday</c> oder
/// <c>yyyy-MM-dd</c>, optional mit <c>HH:mm</c> (Ortszeit Wien). Ohne Uhrzeit gilt der Produktionstagbeginn 06:00.
/// Das Ergebnis ist ein absoluter UTC-Zeitpunkt.
/// </summary>
internal sealed class SeedTime(ShiftCalendar calendar, DateTimeOffset reference)
{
    private static readonly TimeOnly s_defaultTime = new(6, 0);

    private DateOnly Today => DateOnly.FromDateTime(calendar.ToLocal(reference).DateTime);

    public DateTimeOffset Parse(string text)
    {
        var parts = text.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new FormatException("Leere Zeitangabe im Seed.");

        var day = parts[0] switch
        {
            "today" => Today,
            "tomorrow" => Today.AddDays(1),
            "yesterday" => Today.AddDays(-1),
            var iso => DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        var time = parts.Length == 2 ? TimeOnly.ParseExact(parts[1], "HH:mm", CultureInfo.InvariantCulture) : s_defaultTime;
        return calendar.ToUtc(day.ToDateTime(time));
    }

    public DateTimeOffset? ParseOptional(string? text) => text is null ? null : Parse(text);

    public static DateOnly ParseDay(string text) => DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}

// Formate der Seed-Dateien. Sie sind absichtlich getrennt von den Entitäten, damit Zeitangaben als Text stehen können.

internal sealed record ArticlesFile(
    IReadOnlyList<MixtureSeed> Mixtures,
    IReadOnlyList<ArticleSeed> Articles,
    IReadOnlyList<MixtureBatchSeed> MixtureBatches,
    string? Note = null);

internal sealed record MixtureSeed(string Id, string Name, string Grit);

internal sealed record ArticleSeed(
    string Id, string Name, int DiameterMm, string Grit, decimal ThicknessMm, int PiecesPerCarton,
    decimal FillWeightTargetGrams = 0, decimal FillWeightToleranceGrams = 0, string? ArticleGroup = null);

internal sealed record MixtureBatchSeed(
    string Id, string MixtureId, int BulkDensityGramsPerLiter, string ProducedAt, string? Remark, string? ProcessableUntil = null);

internal sealed record ProductionFile(IReadOnlyList<MouldSeed> Moulds, IReadOnlyList<OrderSeed> Orders, string? Note = null);

internal sealed record MouldSeed(
    string Id, string StationId, int DiameterMm, int StrokesSinceChange, int StrokeLimit, int AverageStrokesPerDay,
    int Nests, string MountedAt);

internal sealed record OrderSeed(
    string Id, string ArticleId, string HallId, string? StationId, int TargetQuantity, int ProducedQuantity,
    OrderState State, string? StartedAt, string? FinishedAt, string Due, string? Remark);

internal sealed record CostRatesFile(
    string Status,
    bool IsCalibrated,
    IReadOnlyDictionary<string, decimal> DowntimeMinuteByStation,
    IReadOnlyDictionary<string, decimal> ScrapPieceByArticle,
    string? Note = null);

internal sealed record MaintenanceFile(
    IReadOnlyList<MaintenancePlanSeed> Plans, IReadOnlyList<MaintenanceHistorySeed> History, string? Note = null);

internal sealed record MaintenancePlanSeed(
    string Id, string StationId, string Title, MaintenanceKind Kind, string Due, string? WindowEnd = null,
    int? DurationMinutes = null, bool SafetyRelevant = false, bool Forecast = false, string? WorkOrder = null,
    string? MouldId = null, string? Hint = null);

internal sealed record MaintenanceHistorySeed(
    string Id, string StationId, string Title, MaintenanceKind Kind, string DoneAt, string? Result);

internal sealed record UsersFile(
    IReadOnlyList<RoleSeed> Roles,
    IReadOnlyList<UserSeed> Users,
    IReadOnlyList<EscalationRuleSeed> EscalationRules,
    string? Note = null);

internal sealed record RoleSeed(
    string Id, string Title, string? ScopeHallId, string ScopeDescription, string Devices,
    IReadOnlyList<string> Rights, string MessageFilter);

internal sealed record UserSeed(string Id, string DisplayName, string RoleId);

internal sealed record EscalationRuleSeed(
    string Id, string Name, string Subject, bool SafetyRelevantOnly, int ThresholdMinutes, string TargetRoleId);

internal sealed record FavoritesFile(IReadOnlyDictionary<string, IReadOnlyList<FavoriteSeed>> ByRole, string? Note = null);

internal sealed record FavoriteSeed(string ShortTitle, string Question);

internal sealed record CalendarFile(
    string FirstDay, string LastDay, IReadOnlyList<HolidaySeed> Holidays, IReadOnlyList<ClosureSeed> Closures, string? Note = null);

internal sealed record HolidaySeed(string Date, string Name);

internal sealed record ClosureSeed(string From, string Until, string Name);
