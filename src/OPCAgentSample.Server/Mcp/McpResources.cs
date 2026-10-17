using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using OPCAgentSample.Domain;
using OPCAgentSample.Domain.Opc;
using OPCAgentSample.Kpi;
using OPCAgentSample.Server.Configuration;
using OPCAgentSample.Server.MasterData;

namespace OPCAgentSample.Server.Mcp;

/// <summary>
/// Referenzwissen als MCP-Resources: Anlagenstruktur, Schichtkalender, Kennzahl-Definitionen und das NodeId-Schema einer Station.
/// Die Struktur richtet sich nach der Rolle des Clients; Halle B und C sehen nur Produktionsleiter.
/// </summary>
[McpServerResourceType]
public sealed class PlantResources(
    McpCaller caller,
    McpSession session,
    ShiftCalendar calendar,
    MasterDataStore masterData,
    IOptions<PlantOptions> plant)
{
    private static readonly object[] s_shifts =
    [
        new { Name = "Früh", Code = "F", Von = "06:00", Bis = "14:00" },
        new { Name = "Spät", Code = "S", Von = "14:00", Bis = "22:00" },
        new { Name = "Nacht", Code = "N", Von = "22:00", Bis = "06:00" },
    ];

    private static readonly string[] s_rules =
    [
        "Produktion Montag 06:00 bis Samstag 06:00, Wochenende und Feiertage frei.",
        "Die Nachtschicht gehört zum Produktionstag ihres Beginns (22:00); ein Produktionstag läuft von 06:00 bis 06:00.",
        "Die Woche (KW) ist die ISO-Woche, Montag 06:00 bis Montag 06:00.",
        "Zeiträume in den Tools: aktuelle Schicht, vorherige Schicht, heute, gestern, diese Woche, Vorwoche, KW 43, von/bis.",
    ];

    [McpServerResource(UriTemplate = "plant://structure", Name = "plant_structure", Title = "Anlagenstruktur", MimeType = "application/json")]
    [Description("Aufbau des Werks: Hallen, Bereiche (Pressen, Puffer, Öfen, Verpackung) und Stationen mit Typ. Ausmodelliert ist nur Halle A; "
        + "Halle B und C liefern nur Zähler und Zustände für den Werksvergleich.")]
    public async Task<string> StructureAsync(CancellationToken cancellationToken)
    {
        var wholePlant = await caller.SeesWholePlantAsync(cancellationToken);
        var halls = new List<object>
        {
            new
            {
                Id = plant.Value.HallId,
                Titel = $"Halle {plant.Value.HallId}",
                Ausmodelliert = true,
                Bereiche = PlantLayout.HallAStations.GroupBy(s => s.Area).Select(g => new
                {
                    Name = Views.AreaWord(g.Key),
                    Stationen = g.Select(s => new { s.Id, s.Name, Typ = Views.TypeWord(s.Type) }),
                }),
            },
        };
        if (wholePlant)
        {
            halls.AddRange(plant.Value.CounterStations.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (object)new
            {
                Id = p.Key,
                Titel = $"Halle {p.Key}",
                Ausmodelliert = false,
                Hinweis = $"Liefert nur Zähler und Zustände (Station {p.Value}).",
            }));
        }

        return Json(new
        {
            Werk = PlantLayout.PlantId,
            Rolle = (await caller.GetRoleAsync(cancellationToken)).Title,
            Sicht = wholePlant ? "Werk (Hallen A bis C)" : $"Halle {plant.Value.HallId}",
            Hallen = halls,
            Materialfluss = "Pressen (RT-01 bis RT-04) → Puffer PU-OFEN → Öfen (OF-01, OF-02) → Verpackung (VP-01, VP-02)",
        });
    }

    [McpServerResource(UriTemplate = "plant://shift-calendar", Name = "shift_calendar", Title = "Schichtkalender", MimeType = "application/json")]
    [Description("Schichtmodell (Früh 06-14, Spät 14-22, Nacht 22-06), Produktionstage (Mo 06:00 bis Sa 06:00), Feiertage und Betriebsurlaub "
        + "im Datenhorizont sowie die aktuelle Schicht, der Produktionstag und die Kalenderwoche mit ihren Schlüsseln.")]
    public async Task<string> ShiftCalendarAsync(CancellationToken cancellationToken)
    {
        var now = await session.NowAsync(cancellationToken);
        var data = await masterData.GetAsync(cancellationToken);
        var holidays = data.Holidays.OrderBy(h => h.Date).ToList();
        var closed = data.CalendarDays.Where(d => !d.IsProductionDay && d.Remark != null).OrderBy(d => d.ProductionDay).ToList();
        var shift = calendar.ShiftPeriodOf(now);
        var day = calendar.DayPeriodOf(now);
        var week = calendar.WeekPeriodOf(now);
        var upcoming = new List<object>();
        for (var d = calendar.ProductionDayOf(now); upcoming.Count < 10; d = d.AddDays(1))
        {
            if (calendar.IsProductionDay(d)) upcoming.Add(Fmt.Day(d));
        }

        return Json(new
        {
            Zeitzone = calendar.TimeZone.Id,
            Schichten = s_shifts,
            Regeln = s_rules,
            Datenstand = Fmt.Stamp(now),
            Aktuell = new
            {
                Schicht = new { Schluessel = shift.Key, Bezeichnung = ShiftCalendar.ShiftLabel(calendar.ShiftOf(now)), Von = Fmt.Stamp(shift.StartUtc), Bis = Fmt.Stamp(shift.EndUtc) },
                Tag = day.Key,
                Woche = week.Key,
            },
            NaechsteProduktionstage = upcoming,
            Feiertage = holidays.Where(h => h.Date >= DateOnly.FromDateTime(now.UtcDateTime)).Select(h => new { Datum = Fmt.Day(h.Date), h.Name }),
            Betriebsurlaub = closed.Select(c => new { Datum = Fmt.Day(c.ProductionDay), c.Remark }),
        });
    }

    [McpServerResource(UriTemplate = "plant://kpi-definitions", Name = "kpi_definitions", Title = "Kennzahl-Definitionen", MimeType = "application/json")]
    [Description("Definitionen aller Kennzahlen (Planbelegungszeit, Verfügbarkeit, Leistung, Qualität, OEE, Ist/Soll, Taktverlust, MTBF/MTTR, Kosten, "
        + "Materialreichweite) und die Grenzwerte der Ampel. Die Kennzahlen berechnet der Server deterministisch, nie das Modell.")]
    public string KpiDefinitions()
    {
        var t = TrafficLightThresholds.Default;
        return Json(new
        {
            Hinweis = "Hallen- und Zeitraumwerte entstehen aus Summen der Grundgrößen, nie aus gemittelten Prozenten.",
            Kennzahlen = KpiCatalog.Definitions.Select(d => new { d.Name, d.Definition }),
            Ampel = new
            {
                Regel = "Der schlechteste Wert bestimmt die Ampel (Läuft, Warnung, Problem).",
                IstSoll = $"unter {t.PlanWarningBelow * 100:0} % Warnung, unter {t.PlanProblemBelow * 100:0} % Problem; wartet die Station auf Vorstufe oder Material, höchstens Warnung",
                Ueberproduktion = $"über {t.OverproductionAbove * 100:0} % ohne laufenden Auftrag: Warnung",
                Takt = $"über +{t.CycleWarningAbove * 100:0} % Warnung, über +{t.CycleProblemAbove * 100:0} % Problem",
                Fuellgewicht = $"Mittel ab ±{t.FillWeightWarningGrams:0.0} g Warnung, ab ±{t.FillWeightProblemGrams:0.0} g Problem",
                Formstandzeit = $"ab {t.MouldWarningAt * 100:0} % Warnung, ab {t.MouldProblemAt * 100:0} % Problem",
                Materialreichweite = $"unter {t.MaterialWarningMinutes:0} min Warnung, unter {t.MaterialProblemMinutes:0} min Problem",
                Ofen = $"Belegung ab {t.OvenWarningAt * 100:0} % Warnung",
            },
            BewertungOee = "ab 80 % in Ordnung, ab 65 % Warnung, darunter kritisch",
            Einheiten = "Zeiten in Minuten (min) oder Stunden (h), Mengen in Stück, Kosten in Euro; Zeitangaben in Ortszeit Wien.",
        });
    }

    [McpServerResource(UriTemplate = "plant://machine/{id}/addressspace", Name = "machine_addressspace", Title = "Adressraum einer Station", MimeType = "application/json")]
    [Description("NodeId-Schema und Variablen einer Station im OPC-UA-Adressraum (Namespace urn:vompa:opc-agent-sample:plant), z. B. für id RT-04: "
        + "ns=2;s=HalleA.Pressen.RT-04.Prozess.PresskraftAufbauzeit. Zeigt, welche Rohwerte hinter den Kennzahlen stehen.")]
    public async Task<string> AddressSpaceAsync(string id, CancellationToken cancellationToken)
    {
        var station = PlantLayout.HallAStations.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new McpException($"Unbekannte Station '{id}'. Gültig: {string.Join(", ", PlantLayout.HallAStations.Select(s => s.Id))}.");
        await caller.GetRoleAsync(cancellationToken);
        var basePath = NodeIdScheme.Station(station);
        return Json(new
        {
            Station = station.Id,
            Typ = Views.TypeWord(station.Type),
            Namespace = NodeIdScheme.NamespaceUri,
            Schema = "ns=2;s=Halle<Id>.<Bereich>.<Station>.<Gruppe>.<Variable>",
            Basis = $"ns=2;s={basePath}",
            Gruppen = AddressSpaceCatalog.Groups(station.Type).Select(g => new
            {
                g.Name,
                Variablen = g.Variables.Select(v => new { v.Name, NodeId = $"ns=2;s={basePath}.{g.Name}.{v.Name}", v.Einheit, v.Historisiert }),
            }),
            Hinweis = "Nur lesender Zugriff: Der Adressraum enthält keine schreibbaren Sollwerte und keine Methoden zur Steuerung.",
        });
    }

    private static string Json(object value) => JsonSerializer.Serialize(value, McpJson.Options);
}

/// <summary>Definition einer Kennzahl für die Resource <c>plant://kpi-definitions</c> (Inhalt von <c>docs/calibration.md</c>).</summary>
public sealed record KpiDefinition(string Name, string Definition);

public static class KpiCatalog
{
    public static IReadOnlyList<KpiDefinition> Definitions { get; } =
    [
        new("Planbelegungszeit", "Schichtzeit − Rüsten im Soll − geplante Wartung"),
        new("Verfügbarkeit", "Laufzeit / Planbelegungszeit; jeder ungeplante Stillstand außer Kurzstopps, auch Qualitätssperre"),
        new("Leistung", "Σ(Stück × Soll-Takt) / Laufzeit"),
        new("Qualität", "Σ(Gut × Soll-Takt) / Σ(Stück × Soll-Takt), zeitgewichtet, weil Kartons und Scheiben verschiedene Einheiten sind"),
        new("OEE", "Verfügbarkeit × Leistung × Qualität = Σ(Gut × Soll-Takt) / Planbelegungszeit; Hallenwerte aus Summen, nie aus gemittelten Prozenten"),
        new("Hallen-OEE", "Pressen und Verpackung; Öfen werden über die Belegung bewertet"),
        new("Ist/Soll", "(Gut + Ausschuss) / zeitanteilige Planmenge; Hochrechnung = Ist × Schichtdauer / verstrichene Zeit"),
        new("Auftragsrückstand", "linearer Planfortschritt − gefertigte Menge"),
        new("Taktverlust (Stück)", "Laufzeit × (1/Soll-Takt − 1/Ist-Takt)"),
        new("MTBF / MTTR", "Laufzeit / Störungen bzw. Störungsdauer / Störungen; Anteil ungeplant = Störung / (Störung + geplante Wartung)"),
        new("Kosten", "(ungeplanter Stillstand + Kurzstopps) × Kostensatz der Station + Ausschuss × Stückkosten des Artikels (Kostensätze simuliert)"),
        new("Materialreichweite", "Minimum aus Füllstand / Planverbrauch und „verarbeitbar bis“ (Mischung: letzte Lieferung + Topfzeit)"),
        new("Ofenbelegung", "zeitgewichtete Platzbelegung (belegte Platzminuten / verfügbare Platzminuten)"),
        new("Überproduktion", "Produktion über den Auftrag oder vor Bedarf; keine OEE-Wirkung, eigene Kennzahl"),
    ];
}

/// <summary>Variable eines Adressraum-Knotens einer Station.</summary>
public sealed record AddressVariable(string Name, string? Einheit = null, bool Historisiert = false);

public sealed record AddressGroup(string Name, IReadOnlyList<AddressVariable> Variables);

/// <summary>
/// Gruppen und Variablen je Stationstyp im Informationsmodell (Spezifikation Abschnitt 4). Die Namen sind der ASCII-Vertrag der
/// NodeIds zwischen Simulator und Collector.
/// </summary>
public static class AddressSpaceCatalog
{
    public static IReadOnlyList<AddressGroup> Groups(StationType type) => [.. Common(), .. TypeSpecific(type), Events()];

    private static IEnumerable<AddressGroup> Common()
    {
        yield return new("Identitaet", [new("StationId"), new("Bezeichnung"), new("Bereich"), new("Hersteller"), new("Baujahr")]);
        yield return new("Zustand", [new("Betriebszustand", null, true), new("ZustandSeit"), new("MachineryItemState")]);
        yield return new("Auftrag", [new("AuftragsNr"), new("ArtikelNr"), new("SollMenge"), new("IstMenge"), new("SollTakt", "s"), new("PlanmengeSchicht")]);
        yield return new("Zaehler", [new("StueckGut", null, true), new("StueckAusschuss", null, true), new("Betriebsstunden", "h"), new("ZyklenGesamt")]);
        yield return new("Energie", [new("KwhSeitSchichtbeginn", "kWh", true)]);
        yield return new("Alarme", [new("AktiveAlarme"), new("AktiveMeldungen")]);
    }

    private static AddressGroup Events() => new("Ereignisse", [new("Verluste", null, true), new("Alarmmeldungen", null, true), new("Auftragsereignisse", null, true), new("Zustandswechsel", null, true)]);

    private static IEnumerable<AddressGroup> TypeSpecific(StationType type)
    {
        switch (type)
        {
            case StationType.RotaryTable:
                yield return new("Prozess",
                [
                    new("Taktzeit", "s", true), new("TaktzeitMittel5Min", "s"), new("TaktzeitSoll", "s"), new("Presskraft", "kN", true),
                    new("PresskraftAufbauzeit", "s", true), new("Presszeit", "s"), new("Formtemperatur", "°C", true), new("Fuellgewicht", "g", true),
                    new("FuellgewichtMittel", "g"), new("FuellgewichtSoll", "g"), new("FuellgewichtToleranz", "g"), new("Dicke", "mm", true),
                ]);
                yield return new("Zyklus", [new("SollTaktzeit", "s", true), new("AuftragsNr", null, true), new("ArtikelNr", null, true), new("MischungChargenId", null, true), new("Ausschussgrund", null, true)]);
                yield return new("Material",
                [
                    new("Mischung"), new("ChargenId"), new("FuellstandKg", "kg", true), new("VerarbeitbarBis"), new("Gewebemagazin", "Stück", true), new("Etikettenmagazin", "Stück", true),
                ]);
                yield return new("Werkzeug", [new("FormId"), new("HuebeSeitWechsel"), new("Standzeitgrenze")]);
                yield return new("Hydraulik", [new("Oeltemperatur", "°C", true), new("Systemdruck", "bar", true)]);
                yield return new("Ausschussgruende", [new("Gewicht"), new("Dicke"), new("Gewebe"), new("Etikett"), new("Bruch")]);
                break;
            case StationType.Oven:
                yield return new("Kapazitaet", [new("Chargenplaetze"), new("BelegtePlaetze", null, true), new("Belegung", "%"), new("BelegungAktuell", "%")]);
                yield return new("LaufendeCharge", [new("ChargenId"), new("Durchmesser", "mm"), new("Ofenplatten"), new("Profilschritt"), new("RestzeitMin", "min")]);
                yield return new("Temperatur",
                [
                    new("Zone1Ist", "°C", true), new("Zone2Ist", "°C", true), new("Zone3Ist", "°C", true), new("Zone1Soll", "°C"), new("Zone2Soll", "°C"), new("Zone3Soll", "°C"),
                ]);
                yield return new("Planung", [new("NaechsteFreigabe"), new("Zuordnungsregel")]);
                yield return new("Sicherheit", [new("TuerverriegelungZustand"), new("LetztePruefung")]);
                break;
            case StationType.Buffer:
                yield return new("Bestand", [new("Kapazitaet"), new("AnzahlPlatten", null, true), new("MittlereWartezeit", "min", true), new("AeltesteWartezeit", "min", true)]);
                break;
            case StationType.Packaging:
                yield return new("Verpackung",
                [
                    new("Kartontyp"), new("StueckJeKarton"), new("KartonsIst"), new("KartonsSoll"), new("KontrollwaageFehlmengen", null, true),
                    new("Kartonmagazin", "Stück", true), new("Etikettenmagazin", "Stück", true), new("UmreiferZustand"),
                ]);
                break;
            default:
                break;
        }
    }
}
