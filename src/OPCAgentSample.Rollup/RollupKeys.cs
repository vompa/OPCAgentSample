namespace OPCAgentSample.Rollup;

/// <summary>
/// Einzige Stelle, die die Schlüssel der Verdichtung bildet (Spezifikation Abschnitt 15). Teile mit Doppelpunkt sind in Stationen,
/// Variablen und Perioden ausgeschlossen, damit kein Schlüssel einen fremden Bereich trifft. Die Version <c>v1</c> der Prozessstatistik
/// steht im Schlüssel: Ändern sich Histogrammgrenzen, wird die Statistik unter <c>v2</c> neu aufgebaut.
/// </summary>
public static class RollupKeys
{
    public const string StatsVersion = "v1";

    /// <summary>Zähler einer Station und Periode (Hash).</summary>
    public static string Counters(string station, RollupLevel level, string period) => $"kc:{station}:{level.Code()}:{period}";

    /// <summary>Stillstandsdauer je Grund in Ticks (Sorted Set).</summary>
    public static string ReasonDuration(string station, RollupLevel level, string period) => $"kp:{station}:{level.Code()}:{period}";

    /// <summary>Stillstandsanzahl je Grund (Sorted Set).</summary>
    public static string ReasonCount(string station, RollupLevel level, string period) => $"kn:{station}:{level.Code()}:{period}";

    /// <summary>Prozessstatistik einer Reihe: ein Hash je Behälter (Produktionstag, Quartal, Jahr oder gesamt), Feld = Zeitfach.</summary>
    public static string Stats(string station, string variable, string resolution, string container) =>
        $"ps:{StatsVersion}:{station}:{variable}:{resolution}:{container}";

    /// <summary>Angaben zur Schicht, die keinen Zähler tragen (Hash, z. B. Hallentemperatur zu Schichtbeginn).</summary>
    public static string ShiftInfo(string shiftKey) => $"sh:{shiftKey}";

    public static string LossStream(string hall) => $"lo:{hall}";

    public static string LossOpen(string hall) => $"lo:{hall}:open";

    /// <summary>Verlustereignis-Id → Stream-Id (macht das Schreiben idempotent und erlaubt das Ersetzen).</summary>
    public static string LossDone(string hall) => $"lo:{hall}:done";

    public static string AlarmStream(string hall) => $"al:{hall}";

    public static string AlarmOpen(string hall) => $"al:{hall}:open";

    public static string AlarmDone(string hall) => $"al:{hall}:done";

    /// <summary>Zustandswechsel einer Station (Stream, Id = Zeitpunkt).</summary>
    public static string StateStream(string station) => $"sc:{station}";

    /// <summary>Auftragsereignisse einer Halle (Stream, Id = Zeitpunkt).</summary>
    public static string OrderStream(string hall) => $"oe:{hall}";

    /// <summary>Ringpuffer der letzten 1000 Takte einer Maschine (Stream, Id = Zeitpunkt des Takts).</summary>
    public static string RingBuffer(string station) => $"rb:{station}";

    /// <summary>Letzte Minutenwerte einer Variablen (Stream, Id = Minute, vier Stunden).</summary>
    public static string MinuteRing(string station, string variable) => $"mv:{station}:{variable}";

    /// <summary>Zuletzt gemeldeter Artikel einer Verpackung (Hash, Feld <c>a</c>): Verpackungen führen keinen Ringpuffer.</summary>
    public static string LastKnown(string station) => $"last:{station}";

    /// <summary>Gefertigte Takte eines Auftrags (Hash).</summary>
    public static string OrderCount(string orderId) => $"ord:{orderId}";

    /// <summary>Hübe einer Pressform (Hash).</summary>
    public static string MouldCount(string mouldId) => $"mould:{mouldId}";

    /// <summary>Wasserstände der Quellen (Hash Quelle → UTC-Ticks): bis hierhin (exklusiv) ist alles aufgenommen.</summary>
    public const string Watermarks = "wm";

    /// <summary>Beginn der ersten Schicht mit Rohdaten (String, UTC-Ticks): davor gibt es nur Tageszähler.</summary>
    public const string RawStart = "meta:rawstart";

    // Rückverfolgung
    public static string Lot(string lotId) => $"tr:lot:{lotId}";

    public static string LotCharges(string lotId) => $"tr:lot:{lotId}:chg";

    public static string BatchLots(string batchId) => $"tr:batch:{batchId}";

    public static string Charge(string chargeId) => $"tr:chg:{chargeId}";

    public static string ChargeLots(string chargeId) => $"tr:chg:{chargeId}:lots";

    public static string ChargePallets(string chargeId) => $"tr:chg:{chargeId}:pal";

    /// <summary>Ofenchargen nach Entladezeit (Sorted Set, Score = Millisekunden).</summary>
    public const string ChargeIndex = "tr:chg:idx";

    public static string Pallet(string palletId) => $"tr:pal:{palletId}";

    public static string PalletCharges(string palletId) => $"tr:pal:{palletId}:chg";

    /// <summary>Alle Schlüssel des Zählers einer Ebene, um sie zu laden (Suchmuster).</summary>
    public static string CountersPattern(RollupLevel level) => $"kc:*:{level.Code()}:*";
}
