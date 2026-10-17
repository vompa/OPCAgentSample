using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OPCAgentSample.Domain;
using OPCAgentSample.Rollup;
using OPCAgentSample.Server.Demo;

namespace OPCAgentSample.Server.Mcp;

/// <summary>
/// Rückverfolgung Mischungscharge → Rundtisch/Schicht → Ofencharge → Palette/Karton (inklusive Rotations- und Bruchprüfung je
/// Ofencharge) und das Postfach der Rolle aus den Eskalationsregeln.
/// </summary>
[McpServerToolType]
public sealed class TraceAndActionTools(
    McpSession session,
    McpCaller caller,
    ScopeResolver scopes,
    PlantQueries queries,
    IInboxSource inbox)
{

    [McpServerTool(Name = "trace_batch", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Verfolgt eine Charge durch die Fertigung: von einer Mischungscharge (z. B. M-2410-17) vorwärts über die Pressenlose (Rundtisch, Schicht, "
        + "Stück) und die Ofenchargen (Ofen, Platten, Rotations- und Bruchprüfung) bis zu den Paletten und Kartons mit Auslieferungsstatus; von einer "
        + "Ofencharge oder Palette aus auch rückwärts bis zur Mischungscharge. Die Zusammenfassung nennt, wie viele Kartons Scheiben der Charge enthalten "
        + "und wie viele davon noch nicht ausgeliefert sind (Sperre oder Rückruf möglich). Eine Palette kann auch Scheiben anderer Chargen enthalten; "
        + "das ist je Palette vermerkt. Nächster Schritt: get_process_trend, um die Qualitätsabweichung der Charge zu belegen.")]
    public async Task<CallToolResult> TraceBatch(
        [Description("Mischungscharge (M-2410-17), Ofencharge oder Palette aus einem früheren Ergebnis.")] string charge,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(charge)) throw new McpException("Bitte eine Charge angeben, z. B. M-2410-17.");

        await scopes.ResolveAsync((string?)null, cancellationToken);
        var id = charge.Trim();
        var now = await session.NowAsync(cancellationToken);
        session.Sources.Add("Redis (Rückverfolgung)", now);

        string kind;
        IReadOnlyList<PressLotRow> lots;
        IReadOnlyList<OvenChargeRow> charges;
        IReadOnlyList<PalletRow> pallets;
        if (await queries.ExistsAsync(TraceKind.Pallet, id))
        {
            kind = "Palette";
            pallets = await queries.PalletsAsync(null, [id]);
            var chargeIds = pallets.SelectMany(p => p.ChargeIds).Distinct().ToList();
            charges = await ChargesAsync(chargeIds);
            lots = await LotsOfChargesAsync(chargeIds);
        }
        else if (await queries.ExistsAsync(TraceKind.OvenCharge, id))
        {
            kind = "Ofencharge";
            charges = await queries.OvenChargesAsync(now, now, null, id);
            lots = await LotsOfChargesAsync([id]);
            pallets = await queries.PalletsAsync([id], null);
        }
        else
        {
            lots = await queries.PressLotsAsync(id, null);
            if (lots.Count == 0 && !await queries.BatchExistsAsync(id))
            {
                throw new McpException($"Die Charge '{id}' ist unbekannt. Gültig sind Mischungschargen (M-2410-17), Ofenchargen und Paletten.");
            }

            kind = "Mischungscharge";
            var links = await queries.ChargeLotsAsync([.. lots.Select(l => l.LotId)], null);
            var chargeIds = links.Select(l => l.ChargeId).Distinct().ToList();
            charges = await ChargesAsync(chargeIds);
            pallets = await queries.PalletsAsync(chargeIds, null);
        }

        var tracedCharges = charges.Select(c => c.ChargeId).ToHashSet(StringComparer.Ordinal);
        var cartons = pallets.Sum(p => p.Cartons);
        var notShipped = pallets.Where(p => p.ShippedUtc is null).Sum(p => p.Cartons);
        var failed = charges.Where(c => !c.RotationTestPassed || !c.BreakTestPassed).ToList();
        var batches = lots.Select(l => l.BatchId).Distinct().Order().ToList();

        return await new Reply("trace_batch")
            .Add("ausgang", new { Art = kind, Id = id })
            .Add("mischungschargen", batches)
            .Add("zusammenfassung", new
            {
                Pressenlose = lots.Count,
                StueckGepresst = lots.Sum(l => (long)l.GoodPieces),
                Ofenchargen = charges.Count,
                Paletten = pallets.Count,
                Kartons = cartons,
                KartonsAusgeliefert = cartons - notShipped,
                KartonsNichtAusgeliefert = notShipped,
                PruefungenBestanden = failed.Count == 0,
                Text = $"{cartons} Kartons auf {pallets.Count} Paletten enthalten Scheiben aus {string.Join(", ", batches)}; "
                    + $"davon sind {notShipped} Kartons noch nicht ausgeliefert, {cartons - notShipped} bereits ausgeliefert.",
            })
            .Add("pressenlose", lots.Take(40).Select(l => new
            {
                Los = l.LotId,
                Station = l.StationId,
                Charge = l.BatchId,
                l.ArticleId,
                Schicht = l.ShiftKey,
                Von = Fmt.Stamp(l.StartUtc),
                Bis = Fmt.Stamp(l.EndUtc),
                Stueck = l.GoodPieces,
            }))
            .Add("ofenchargen", charges.Take(40).Select(c => new
            {
                Charge = c.ChargeId,
                Ofen = c.OvenId,
                Platz = c.Place,
                DurchmesserMm = c.DiameterMm,
                Platten = c.Plates,
                Beladen = Fmt.Stamp(c.LoadedUtc),
                Entladen = Fmt.Stamp(c.UnloadedUtc),
                Rotationspruefung = c.RotationTestPassed ? "bestanden" : "nicht bestanden",
                Bruchpruefung = c.BreakTestPassed ? "bestanden" : "nicht bestanden",
            }))
            .Add("paletten", pallets.Take(40).Select(p => new
            {
                Palette = p.PalletId,
                Verpackung = p.StationId,
                p.ArticleId,
                p.Cartons,
                StueckJeKarton = p.PiecesPerCarton,
                Stueck = p.Cartons * p.PiecesPerCarton,
                Erstellt = Fmt.Stamp(p.CreatedUtc),
                Ausgeliefert = p.ShippedUtc is not null,
                AusgeliefertAm = p.ShippedUtc is { } shipped ? Fmt.Stamp(shipped) : null,
                EnthaeltAndereChargen = p.ChargeIds.Any(c => !tracedCharges.Contains(c)),
                Ofenchargen = p.ChargeIds,
            }))
            .BuildAsync(session, cancellationToken);
    }

    [McpServerTool(Name = "get_action_items", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Liefert das Postfach der Rolle: Fälle, die der Server aus den Eskalationsregeln deterministisch erzeugt (Rückstand einer Station, "
        + "Mischung läuft aus, offene Zustandswarnung, Qualitätsabweichung einer Charge, überfällige Wartung, ungleiche Ofenbelegung, Wartungsfreigabe, "
        + "gefährdete Aufträge, Zuverlässigkeit). Je Fall Stufe, Priorität, Titel, Details, Frist und die Frage, mit der Claude ihn analysieren kann. "
        + "Die Rolle bestimmt die Fälle: Schichtleiter, Hallenleiterin oder Produktionsleiter sehen nur ihr eigenes Postfach. "
        + "Nächster Schritt: das Tool zur Frage des Falls, z. B. get_order_progress oder get_maintenance_due.")]
    public async Task<CallToolResult> GetActionItems(
        [Description("Rolle: Schichtleiter, Hallenleiterin oder Produktionsleiter (oder shift_lead, hall_lead, production_lead). Standard: die eigene Rolle.")] string? role = null,
        [Description("Halle: A (Standard).")] string? hallId = null,
        CancellationToken cancellationToken = default)
    {
        var own = await caller.GetRoleAsync(cancellationToken);
        var requested = RoleId(role) ?? own.Id;
        if (requested != own.Id)
        {
            throw caller.Deny($"Keine Berechtigung: Das Postfach der Rolle '{role}' ist für {own.Title} nicht sichtbar. Es gibt nur das eigene Postfach.");
        }

        await scopes.ResolveAsync(hallId, cancellationToken);
        var now = await session.NowAsync(cancellationToken);
        var items = await inbox.GetAsync(own.Id, $"mcp:{caller.User.Identity?.Name ?? "client"}", cancellationToken);
        session.Sources.Add("Eskalationsregeln", now);
        return await new Reply("get_action_items")
            .Add("rolle", own.Title)
            .Add("anzahl", items.Count)
            .Add("dringend", items.Count(i => i.Level == Rating.Crit))
            .Add("eintraege", items.Select(i => new
            {
                i.Id,
                Stufe = i.Level,
                StufeText = RatingText.Word(i.Level),
                i.Priority,
                Prioritaet = i.PriorityText,
                Titel = i.Title,
                Details = i.Detail,
                Frist = i.Due,
                Frage = i.Question,
                Uebernommen = i.TakenBy is not null,
            }))
            .BuildAsync(session, cancellationToken);
    }

    private async Task<IReadOnlyList<OvenChargeRow>> ChargesAsync(IReadOnlyCollection<string> chargeIds)
    {
        var rows = new List<OvenChargeRow>();
        foreach (var chargeId in chargeIds)
        {
            rows.AddRange(await queries.OvenChargesAsync(DateTimeOffset.MinValue, DateTimeOffset.MinValue, null, chargeId));
        }

        return [.. rows.OrderBy(r => r.UnloadedUtc)];
    }

    private async Task<IReadOnlyList<PressLotRow>> LotsOfChargesAsync(IReadOnlyCollection<string> chargeIds)
    {
        var links = await queries.ChargeLotsAsync(null, chargeIds);
        return await queries.PressLotsAsync(null, [.. links.Select(l => l.LotId).Distinct()]);
    }

    private static string? RoleId(string? role)
    {
        if (string.IsNullOrWhiteSpace(role)) return null;

        return role.Trim().ToLowerInvariant() switch
        {
            "schichtleiter" or "shift_lead" => "shift_lead",
            "hallenleiterin" or "hallenleiter" or "hall_lead" => "hall_lead",
            "produktionsleiter" or "produktionsleiterin" or "production_lead" => "production_lead",
            _ => role.Trim(),
        };
    }
}
