using System.ComponentModel;
using ModelContextProtocol.Server;

namespace OPCAgentSample.Server.Mcp;

/// <summary>
/// Vorlagen für wiederkehrende Aufgaben der drei Rollen. Jede Vorlage nennt die Reihenfolge der Tools und die Form der Antwort
/// (Antwortkarte: Kernaussage, Was tun?, Die Werte, Warum, Datenstand). Die Kennzahlen berechnet der Server; das Modell wählt Tools,
/// verknüpft die Ergebnisse und formuliert.
/// </summary>
[McpServerPromptType]
public sealed class McpPrompts
{
    private const string AnswerForm =
        "Antworte auf Deutsch in dieser Reihenfolge: 1. Kernaussage in einem Satz mit den wichtigsten Zahlen, 2. Was tun? (konkrete nächste "
        + "Schritte), 3. Die Werte (Tabelle mit Bewertung als Wort: in Ordnung, Warnung, kritisch), 4. Warum (Zusammenhänge, z. B. Ursachenketten), "
        + "5. Datenstand und Quellen aus den Tool-Ergebnissen (source_until, quelle). Nutze nur Zahlen aus den Tool-Ergebnissen, rechne keine Kennzahlen selbst "
        + "und erfinde nichts. Du kannst nur lesen und nichts an den Maschinen steuern. Nenne keine Personen im Zusammenhang mit Maschinendaten.";

    [McpServerPrompt(Name = "schichtuebergabe", Title = "Schichtübergabe")]
    [Description("Übergabe der vorherigen Schicht an den Schichtleiter: Planerfüllung, offene Störungen, Material, Wartung.")]
    public static string Schichtuebergabe(
        [Description("Schicht, die übergeben wird: vorherige (Standard), aktuell, Früh, Spät, Nacht.")] string schicht = "vorherige") =>
        $"Bereite die Schichtübergabe für den Schichtleiter von Halle A vor: Was muss er von der Schicht '{schicht}' wissen?\n"
        + "Vorgehen:\n"
        + $"1. get_shift_summary (scope Halle A, schicht '{schicht}'): Planerfüllung der Pressen, Ist/Soll je Station, Stillstand, OEE.\n"
        + "2. get_active_alarms (scope Halle A): offene Störungen und Warnungen.\n"
        + "3. get_order_progress (scope Halle A): Aufträge und Rückstände.\n"
        + "4. get_material_reach (scope Halle A): was reicht nicht bis zur nächsten Anlieferung, auch wenn der Füllstand es zeigt.\n"
        + "5. get_maintenance_due (scope Halle A, horizont Schicht): Wartungen in der nächsten Schicht und was überfällig ist.\n"
        + "Nenne zuerst die Planerfüllung, dann die drei wichtigsten Punkte, die sofort Aufmerksamkeit brauchen. " + AnswerForm;

    [McpServerPrompt(Name = "tagesbesprechung_halle", Title = "Tagesbesprechung Halle")]
    [Description("Vorbereitung der Tagesbesprechung der Hallenleiterin: gestriger Tag je Schicht, größte Verlustursachen, Wartung heute, Material.")]
    public static string TagesbesprechungHalle() =>
        "Bereite die Tagesbesprechung für die Hallenleiterin von Halle A vor.\n"
        + "Vorgehen:\n"
        + "1. get_shift_summary (scope Halle A, schicht gestern, alle true): OEE je Schicht von gestern mit Verfügbarkeit, Leistung und Qualität.\n"
        + "2. get_downtime_pareto (scope Halle A, zeitraum gestern): Hauptgrund der schwächsten Schicht.\n"
        + "3. check_maintenance_conflicts (hallId A, zeitraum heute): Wartung und Rüsten heute, Bündelungsmöglichkeiten.\n"
        + "4. get_material_reach (scope Halle A): Material, das in der Nacht knapp werden kann.\n"
        + "Vergleiche die Schichten, benenne den Unterschied (meist Verfügbarkeit) und eine Maßnahme für heute. " + AnswerForm;

    [McpServerPrompt(Name = "wochenreport", Title = "Wochenreport")]
    [Description("Wochenbericht für den Produktionsleiter: Planerfüllung, OEE gegenüber der Vorwoche, Ausschuss, Kosten, gefährdete Aufträge, Zuverlässigkeit.")]
    public static string Wochenreport(
        [Description("Woche: diese Woche (Standard), Vorwoche oder KW 42.")] string woche = "diese Woche") =>
        $"Fasse die Woche '{woche}' für den Produktionsleiter zusammen.\n"
        + "Vorgehen:\n"
        + $"1. get_oee (scope Werk, zeitraum '{woche}', gruppierung Halle): OEE je Halle.\n"
        + "2. compare (dimension Woche, kpi OEE, zeitraum 'KW 41-42' bzw. die letzten beiden Wochen): Entwicklung gegenüber der Vorwoche.\n"
        + $"3. get_downtime_pareto (scope Werk, zeitraum '{woche}', bewertung Kosten): Kosten von Ausschuss und Stillstand, größter Posten.\n"
        + "4. get_order_progress (scope Werk, filter gefährdet): Aufträge, die den Termin verfehlen.\n"
        + "5. get_reliability_kpis (scope Werk, zeitraum die Woche): MTBF, MTTR und Anteil ungeplanter Stillstände.\n"
        + "6. get_overproduction (scope Halle A): Überproduktion und ihre Folgen.\n"
        + "Stelle Erfüllung, OEE und Risiken an den Anfang. " + AnswerForm;

    [McpServerPrompt(Name = "weihnachtsstillstand", Title = "Weihnachtsstillstand")]
    [Description("Planung des Betriebsurlaubs 24.12.2026 bis 06.01.2027 als Wartungsfenster: Revisionen, Inspektionen, Prüfungen, Vorlauf in den letzten Produktionstagen.")]
    public static string Weihnachtsstillstand() =>
        "Was machen wir im Weihnachtsstillstand (Betriebsurlaub 24.12.2026 bis 06.01.2027)? Plane das Wartungsfenster für Halle A.\n"
        + "Vorgehen:\n"
        + "1. Lies die Resource plant://shift-calendar: Feiertage, Betriebsurlaub und letzte Produktionstage davor.\n"
        + "2. get_maintenance_due (scope Halle A, von 2026-12-01, bis 2027-01-17, prognose true): Ofenrevisionen, Generalinspektionen, jährliche Sicherheitsprüfungen, "
        + "Waagen-Kalibrierung im Jänner.\n"
        + "3. get_maintenance_due (scope Halle A, filter überfällig): was bis dahin nicht mehr offen sein darf.\n"
        + "4. get_reliability_kpis (scope Halle A, zeitraum 90 Tage, gruppierung Station): welche Station am meisten Instandsetzung braucht.\n"
        + "5. get_condition_warnings (scope Halle A): Zustandswarnungen, die im Stillstand behoben werden sollten.\n"
        + "Schlage eine Reihenfolge der Arbeiten im Fenster vor (sicherheitsrelevant zuerst) und nenne, was vor dem Stillstand vorbereitet werden muss. " + AnswerForm;
}
