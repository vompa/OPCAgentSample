using OPCAgentSample.Domain.History;

namespace OPCAgentSample.Simulator.Model;

/// <summary>Datentyp einer Variablen des Anlagenmodells.</summary>
internal enum ValueKind
{
    Double,
    Int32,
    Int64,
    String,
    Boolean,
    DateTime,
    OperatingState,
    ScrapGrade,
}

/// <summary>Woher die Historie einer Variablen stammt.</summary>
internal abstract record HistoryBinding;

/// <summary>Wert je Takt aus <see cref="CycleRecord"/> (nur Rundtische).</summary>
internal sealed record CycleHistory(Func<CycleRecord, object> Select) : HistoryBinding;

/// <summary>Minutenwert (<see cref="MinuteValue"/>) mit dem Namen aus <see cref="MinuteVariables"/>.</summary>
internal sealed record MinuteHistory(string Variable) : HistoryBinding;

/// <summary>Betriebszustand aus den Zustandswechseln der Station.</summary>
internal sealed record StateHistory : HistoryBinding;

/// <summary>Datensatz der Domäne als JSON-String (Ereignis-Variablen).</summary>
internal sealed record RecordHistory(Type RecordType) : HistoryBinding;

/// <summary>Variable eines Typs: Name, Datentyp, Einheit (UNECE-Code) und ggf. Historie.</summary>
internal sealed record VariableSpec(string Name, ValueKind Kind, UnitInfo? Unit = null, HistoryBinding? History = null);

/// <summary>Gruppe (Ordner) mit ihren Variablen; optionale Gruppen entsprechen der Modellierungsregel "Optional".</summary>
internal sealed record GroupSpec(string Name, bool Optional, IReadOnlyList<VariableSpec> Variables);

/// <summary>ObjectType des Informationsmodells mit seinen Gruppen (ohne die der Basistypen).</summary>
internal sealed record TypeSpec(string Name, string? BaseName, string Description, IReadOnlyList<GroupSpec> Groups);

/// <summary>Einheit mit Anzeigesymbol und UNECE-Code.</summary>
internal sealed record UnitInfo(string Symbol, string Code);

/// <summary>Einheiten nach UNECE Recommendation 20 (Anzeigename und Code).</summary>
internal static class Units
{
    public static readonly UnitInfo Celsius = new("°C", "CEL");
    public static readonly UnitInfo Second = new("s", "SEC");
    public static readonly UnitInfo KiloNewton = new("kN", "B47");
    public static readonly UnitInfo Gram = new("g", "GRM");
    public static readonly UnitInfo Millimetre = new("mm", "MMT");
    public static readonly UnitInfo Kilogram = new("kg", "KGM");
    public static readonly UnitInfo Bar = new("bar", "BAR");
    public static readonly UnitInfo Minute = new("min", "MIN");
    public static readonly UnitInfo Percent = new("%", "P1");
    public static readonly UnitInfo KiloWattHour = new("kWh", "KWH");
    public static readonly UnitInfo Hour = new("h", "HUR");
    public static readonly UnitInfo Piece = new("Stk", "H87");
}
