using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Export;
using Raffaello.Core.Materials;
using Raffaello.Core.Queue;

namespace Raffaello.Core.Insights;

/// <summary>
/// The insights module end to end: collects the documents behind the data, hashes them (cached), runs every detector, applies
/// dismissals, and turns the result into "Needs you today" items, Excel sheets and the anomalies page of the invoice package.
/// </summary>
public static class InsightsEngine
{
    public const string NavKey = "Anomalies";

    /// <summary>Every document / photo the data points at (attachments, height photos, length markups).</summary>
    public static List<DocumentUse> CollectDocuments(ProjectSnapshot s)
    {
        var res = new List<DocumentUse>();
        foreach (var a in s.Attachments.Where(a => a.FilePath.Length > 0))
        {
            var parts = a.OwnerKey.Split('|');
            var sub = a.OwnerKind == AttachmentKinds.Invoice && parts.Length >= 3 ? parts[1] : s.Statements.FirstOrDefault(st => st.StatementNo.Equals(a.OwnerKey, StringComparison.OrdinalIgnoreCase))?.Subcontractor ?? "";
            var inv = a.OwnerKind == AttachmentKinds.Invoice && parts.Length >= 3 && int.TryParse(parts[2], out var n) ? n : 0;
            res.Add(new DocumentUse(a.FilePath, $"{a.OwnerKind} {a.OwnerKey} ({a.Kind})", EvidenceKinds.Document, a.Id, sub, inv));
        }
        foreach (var c in s.Claims)
        {
            if (c.HeightPhoto.Length > 0) res.Add(new DocumentUse(c.HeightPhoto, $"ledger #{c.Id} {c.Subcontractor} INV {c.InvoiceNo} {c.Room} >4.5 m photo", EvidenceKinds.Ledger, c.Id, c.Subcontractor, c.InvoiceNo));
            if (c.LengthAttachment.Length > 0) res.Add(new DocumentUse(c.LengthAttachment, $"ledger #{c.Id} {c.Subcontractor} INV {c.InvoiceNo} {c.Room} 15 m markup", EvidenceKinds.Ledger, c.Id, c.Subcontractor, c.InvoiceNo));
        }
        return res;
    }

    /// <summary>Hashes the documents (cache in the insights store) and returns all hashes.</summary>
    public static List<InsightFileHash> HashDocuments(IInsightsStore? store, InsightsData data, IReadOnlyList<DocumentUse> docs, ImageDecoder? decoder, DateTime now)
    {
        var (all, changed) = DocumentHashes.Hash(docs.Select(d => d.Path), data.FileHashes, decoder, now);
        if (changed.Count > 0 && store != null)
            try { store.SaveFileHashes(changed); } catch (Exception) { /* the cache is an optimisation only */ }
        return all;
    }

    /// <summary>Claim anomalies + material reconciliation warnings + earned-value early warnings, dismissals applied.</summary>
    public static List<Anomaly> All(AnomalyInputs i, ReconResult? recon = null, EvResult? ev = null)
    {
        var list = AnomalyDetector.Detect(i);
        var extra = new List<Anomaly>();
        if (recon != null) extra.AddRange(MaterialReconciliation.ToAnomalies(recon));
        if (ev != null) extra.AddRange(EarnedValue.ToAnomalies(ev));
        if (extra.Count > 0)
        {
            var dismissed = i.Data.ActiveDismissals();
            foreach (var a in extra.GroupBy(a => a.Fingerprint).Select(g => g.First()))
            {
                if (dismissed.TryGetValue(a.Fingerprint, out var d)) a.Dismissal = d;
                if (list.All(x => x.Fingerprint != a.Fingerprint)) list.Add(a);
            }
            list = list.OrderByDescending(a => a.Severity).ThenByDescending(a => a.Score).ToList();
        }
        return list;
    }

    // ------------------------------------------------------------------ Needs you today

    /// <summary>HIGH insights one by one (top few per kind), MEDIUM grouped per kind. Dismissed insights are left out.</summary>
    public static IEnumerable<QueueItem> QueueItems(IEnumerable<Anomaly> anomalies, int perKind = 3)
    {
        var live = anomalies.Where(a => !a.IsDismissed).ToList();
        foreach (var g in live.Where(a => a.Severity == InsightSeverity.High).GroupBy(a => a.Kind))
        {
            var list = g.OrderByDescending(a => a.Score).ToList();
            foreach (var a in list.Take(perKind))
                yield return new QueueItem(Verdict.Check, "INSIGHT", a.Title, $"{a.Kind}: {a.SuggestedAction}", new NavTarget(NavKey, Key: a.Fingerprint), 2.2e8 + Math.Min(1e7, a.Score));
            if (list.Count > perKind)
                yield return new QueueItem(Verdict.Check, "INSIGHT", $"{list.Count - perKind} more {g.Key} warnings (high)", "Open Insights > Anomalies filtered to this kind.", new NavTarget(NavKey, Key: "KIND|" + g.Key), 2.1e8 + list.Count);
        }
        foreach (var g in live.Where(a => a.Severity == InsightSeverity.Medium).GroupBy(a => a.Kind))
            yield return new QueueItem(Verdict.Open, "INSIGHT", $"{g.Count()} {g.Key} warnings", g.OrderByDescending(a => a.Score).First().Title, new NavTarget(NavKey, Key: "KIND|" + g.Key), 0.95e8 + g.Count());
    }

    // ------------------------------------------------------------------ Excel

    public static ExportSheet AnomalySheet(IEnumerable<Anomaly> anomalies, string scope, bool includeDismissed = true) => new()
    {
        Name = "ANOMALIES", Title = "CLAIM ANOMALIES AND INSIGHTS", Subtitle = scope + "  |  warnings only - nothing here blocks a claim or an invoice",
        Columns = new() { new("SEVERITY"), new("KIND", ColumnKind.Text, 18), new("SUBCONTRACTOR", ColumnKind.Text, 22), new("INVOICE"), new("ROOM"), new("STAGE"), new("ITEM"), new("WARNING", ColumnKind.Text, 70),
            new("EXPLANATION", ColumnKind.Text, 90), new("SUGGESTED ACTION", ColumnKind.Text, 60), new("EVIDENCE", ColumnKind.Text, 70), new("DISMISSED"), new("DISMISS REASON", ColumnKind.Text, 40), new("DISMISSED BY") },
        Rows = anomalies.Where(a => includeDismissed || !a.IsDismissed).Select(a => new object?[]
        {
            a.Tag, a.Kind, a.Subcontractor, a.InvoiceText, a.Room, a.Stage, a.Item, a.Title, a.Explanation, a.SuggestedAction, a.EvidenceText,
            a.IsDismissed ? "YES" : "", a.Dismissal?.Reason, a.Dismissal is { } d ? $"{d.DismissedBy} {d.DismissedAt:dd-MMM-yy}" : "",
        }).ToList(),
    };

    public static ExportSheet ReconSheet(ReconResult r, string scope) => new()
    {
        Name = "MATERIAL RECON", Title = "MATERIALS - DELIVERED VS INSTALLED VS PAID", Subtitle = scope + (r.UsingDefaultNorms ? "  |  DEFAULT norms (assumptions)" : ""),
        Columns = new() { new("MATERIAL", ColumnKind.Text, 22), new("UNIT"), new("NORM (PER POINT)", ColumnKind.Text, 46), new("INSTALLED POINTS", ColumnKind.Number), new("PROJECT POINTS", ColumnKind.Number),
            new("THEORETICAL USE", ColumnKind.Number), new("DELIVERED", ColumnKind.Number), new("INVOICED", ColumnKind.Number), new("PAID", ColumnKind.Number), new("ON SITE / WASTED", ColumnKind.Number),
            new("IMPLIED WASTAGE %", ColumnKind.Percent), new("PROJECT NEED", ColumnKind.Number), new("OVER-DELIVERED", ColumnKind.Number), new("OBSERVED / POINT", ColumnKind.Number), new("STATUS"), new("EXPLANATION", ColumnKind.Text, 80) },
        Rows = r.Rows.Select(x => new object?[] { x.Material, x.Unit, x.Scope, x.InstalledPoints, x.PlannedPoints, x.Theoretical, x.Delivered, x.Invoiced, x.Paid, x.OnSite, x.ImpliedWastagePct,
            x.TheoreticalTotal, x.OverDelivered, x.ObservedPerPoint, x.Status, x.Explanation }).ToList(),
    };

    public static ExportSheet MosSheet(ReconResult r, string scope) => new()
    {
        Name = "MOS RELEASE", Title = "OWNER MOS - RELEASE CANDIDATES", Subtitle = scope + "  |  material valued as on site that the installed points have used",
        Columns = new() { new("BOQ CODE", ColumnKind.Text, 22), new("DESCRIPTION", ColumnKind.Text, 50), new("MATERIAL"), new("DELIVERED", ColumnKind.Number), new("INSTALLED (MOS)", ColumnKind.Number),
            new("INSTALLED (THEORETICAL)", ColumnKind.Number), new("RELEASE QTY", ColumnKind.Number), new("BOQ RATE", ColumnKind.Money), new("MOS %", ColumnKind.Percent), new("RELEASE VALUE", ColumnKind.Money) },
        Rows = r.Mos.Select(m => new object?[] { m.BoqCode, m.Description, m.Material, m.Delivered, m.InstalledRecorded, m.InstalledTheoretical, m.ReleaseQty, m.BoqRate, m.MosPct, m.ReleaseValue }).ToList(),
    };

    // ------------------------------------------------------------------ invoice package page

    /// <summary>Insights for one subcontractor invoice (anything naming the subcontractor and the invoice no., or the subcontractor without an invoice no.).</summary>
    public static List<Anomaly> ForInvoice(IEnumerable<Anomaly> all, string sub, int invoiceNo) =>
        all.Where(a => a.Subcontractor.Split(" / ").Any(x => x.Trim().Equals(sub.Trim(), StringComparison.OrdinalIgnoreCase)) && (a.InvoiceNo == invoiceNo || a.InvoiceNo == 0))
           .OrderByDescending(a => a.Severity).ThenByDescending(a => a.Score).ToList();

    /// <summary>PDF "insights check" page for the invoice package (deterministic for the same inputs and stamp).</summary>
    public static void PackagePdf(string path, string title, IReadOnlyList<Anomaly> list, DateTime stamp)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        const string red = "#8B0000";
        Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A4.Landscape());
            page.Margin(26);
            page.DefaultTextStyle(x => x.FontSize(8));
            page.Header().Column(c =>
            {
                c.Item().Text(title + "  -  insight checks").FontSize(13).Bold().FontColor(red);
                c.Item().Text($"{list.Count(a => !a.IsDismissed)} open warnings ({list.Count(a => a.Severity == InsightSeverity.High && !a.IsDismissed)} high), {list.Count(a => a.IsDismissed)} dismissed with a reason. " +
                              "Warnings never block the invoice; they show what was looked at.").FontSize(8).Italic();
            });
            page.Content().PaddingTop(8).Table(tb =>
            {
                tb.ColumnsDefinition(cd => { cd.ConstantColumn(48); cd.ConstantColumn(78); cd.RelativeColumn(3); cd.RelativeColumn(4); cd.RelativeColumn(3); cd.RelativeColumn(2); });
                void H(string t) => tb.Cell().Background("#A6A6A6").Padding(3).Text(t).Bold().FontColor(Colors.Black);
                H("SEVERITY"); H("KIND"); H("WARNING"); H("EXPLANATION / EVIDENCE"); H("SUGGESTED ACTION"); H("DECISION");
                if (list.Count == 0) { tb.Cell().ColumnSpan(6).Padding(4).Text("No warnings for this invoice."); return; }
                foreach (var a in list)
                {
                    var colour = a.IsDismissed ? Colors.Grey.Darken1 : a.Severity == InsightSeverity.High ? red : Colors.Black;
                    tb.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(3).Text(a.Tag).Bold().FontColor(colour);
                    tb.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(3).Text(a.Kind);
                    tb.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(3).Text(a.Title);
                    tb.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(3).Column(c =>
                    {
                        c.Item().Text(a.Explanation);
                        if (a.Evidence.Count > 0) c.Item().PaddingTop(2).Text("Evidence: " + a.EvidenceText).FontSize(7).FontColor(Colors.Grey.Darken2);
                    });
                    tb.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(3).Text(a.SuggestedAction);
                    tb.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(3).Text(a.Dismissal is { } d ? $"DISMISSED by {d.DismissedBy} {d.DismissedAt:dd-MMM-yy}: {d.Reason}" : "open");
                }
            });
            page.Footer().AlignRight().Text(t => { t.Span($"Raffaello insights  |  {stamp:dd-MMM-yyyy}  |  page "); t.CurrentPageNumber(); t.Span(" / "); t.TotalPages(); });
        })).WithMetadata(new DocumentMetadata { Title = title + " - insight checks", Author = "Raffaello", Creator = "Raffaello", Producer = "Raffaello", CreationDate = stamp, ModifiedDate = stamp })
          .GeneratePdf(path);
    }
}
