using System.IO.Compression;
using System.Text.RegularExpressions;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.HeadOffice;
using Raffaello.Core.Invoicing;
using Raffaello.Core.Ledger;

namespace Raffaello.Core.Packaging;

public sealed class PackageRequest
{
    public required ProjectSnapshot Snapshot { get; init; }
    public required IReadOnlyList<PlanImage> Plans { get; init; }
    public required InvoiceBuild Build { get; init; }
    public InvoiceHeaderInfo Info { get; init; } = new();
    public required string OutputFolder { get; init; }
    /// <summary>Folder (shared partition) searched recursively for WIR PDFs by WIR no.</summary>
    public string WirFolder { get; init; } = "";
    /// <summary>Tokens: {CONTRACT} {SUB} {INV} {REV}.</summary>
    public string NamePattern { get; init; } = PackageNames.DefaultPattern;
    /// <summary>FINAL needs the signed invoice; DRAFT does not.</summary>
    public bool Final { get; init; }
    /// <summary>Date written into every generated file (defaults to the invoice revision's creation date) - same inputs, same bytes.</summary>
    public DateTime? Stamp { get; init; }
    /// <summary>[phase6] Documents registered from the Aconex document register (doc no., path): WIRs are found here first.</summary>
    public List<(string DocumentNo, string Path)> RegisteredDocuments { get; init; } = new();
    /// <summary>[phase6] Extra files (e.g. 07_MIR_tracker.xlsx for a supplier invoice): name inside the ZIP, path, note.</summary>
    public List<(string Name, string Path, string Note)> ExtraFiles { get; init; } = new();
    /// <summary>[phase6] Plan images in the tracker are recompressed to stay under this total.</summary>
    public long MaxPlanImageBytes { get; init; } = TrackerExportScope.DefaultMaxImageBytes;
}

public sealed record PackageEntry(string Name, long Bytes, string Sha256, string Note);

public sealed class PackageResult
{
    public string ZipPath { get; init; } = "";
    public string Sha256 { get; set; } = "";
    public string Kind { get; init; } = "";
    public List<PackageEntry> Entries { get; } = new();
    public List<string> MissingWirs { get; } = new();
    public List<string> Warnings { get; } = new();
    public string Summary => $"{Path.GetFileName(ZipPath)} ({Kind}): {Entries.Count} files, {new FileInfo(ZipPath).Length / 1024:N0} KB, " +
                             $"{MissingWirs.Count} WIRs missing, SHA-256 {Sha256[..Math.Min(12, Sha256.Length)]}...";
}

public static class PackageNames
{
    public const string DefaultPattern = "{CONTRACT}_{SUB}_INV-{INV}_Rev{REV}";

    public static string FileName(string pattern, SubInvoice h, bool final)
    {
        var name = (string.IsNullOrWhiteSpace(pattern) ? DefaultPattern : pattern)
            .Replace("{CONTRACT}", h.ContractNo).Replace("{SUB}", h.Subcontractor).Replace("{INV}", h.InvoiceNo.ToString()).Replace("{REV}", h.Revision.ToString());
        return Safe(name) + (final ? "" : "_DRAFT") + ".zip";
    }

    public static string Safe(string s) => Regex.Replace(string.Concat((s ?? "").Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)), @"\s+", "_");

    public static string NormalizeWir(string s) => Regex.Replace((s ?? "").ToUpperInvariant(), "[^A-Z0-9]", "");
}

/// <summary>
/// Head-office ZIP for one invoice revision: index (with SHA-256 of every file), template Excel, filtered PDF, signed scan,
/// contract documents, WIR PDFs found by number, site statements, head-office tracker and the checks report.
/// Same inputs give the same bytes (fixed dates, fixed entry order and timestamps).
/// </summary>
public static class InvoicePackageBuilder
{
    public static PackageResult Build(PackageRequest req)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var b = req.Build;
        var h = b.Header;
        var s = req.Snapshot;
        var stamp = Deterministic.Clamp(req.Stamp ?? (h.CreatedAt == default ? DateTime.Today : h.CreatedAt));
        var invKey = Attachment.InvoiceKey(h.ContractNo, h.Subcontractor, h.InvoiceNo, h.Revision);
        var signed = s.Attachments.Where(a => a.OwnerKind == AttachmentKinds.Invoice && a.OwnerKey == invKey && a.Kind == AttachmentKinds.Signed && File.Exists(a.FilePath))
            .OrderByDescending(a => a.AddedAt).FirstOrDefault();
        if (req.Final && signed is null) throw new InvalidOperationException($"{h.Title}: attach the signed invoice before building the FINAL package (a DRAFT package does not need it).");

        Directory.CreateDirectory(req.OutputFolder);
        var zipPath = Path.Combine(req.OutputFolder, PackageNames.FileName(req.NamePattern, h, req.Final));
        var res = new PackageResult { ZipPath = zipPath, Kind = req.Final ? "FINAL" : "DRAFT" };
        var work = Path.Combine(Path.GetTempPath(), "raffaello-pkg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var files = new List<(string Name, byte[] Data, string Note)>();
            string Tmp(string name) => Path.Combine(work, name.Replace('/', '_'));

            var xlsx = Tmp("01_Invoice.xlsx");
            InvoiceExcelExporter.Export(xlsx, b, req.Info, stamp: stamp);
            files.Add(("01_Invoice.xlsx", File.ReadAllBytes(xlsx), "invoice in the template layout"));

            var pdf = Tmp("02_Invoice_filtered.pdf");
            InvoicePdfExporter.Export(pdf, b, req.Info, stamp: stamp);
            files.Add(("02_Invoice_filtered.pdf", File.ReadAllBytes(pdf), "rows with quantities only"));

            if (signed != null) files.Add(("03_Signed_invoice" + Ext(signed.FileName, ".pdf"), File.ReadAllBytes(signed.FilePath), $"signed scan {signed.FileName}"));
            else res.Warnings.Add("Signed invoice not attached - DRAFT package.");

            foreach (var a in s.Attachments.Where(a => a.OwnerKind == AttachmentKinds.Contract && a.OwnerKey.Equals(h.ContractNo, StringComparison.OrdinalIgnoreCase)).OrderBy(a => a.FileName, StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(a.FilePath)) files.Add(($"04_Contract/{PackageNames.Safe(a.FileName)}", File.ReadAllBytes(a.FilePath), "contract document"));
                else res.Warnings.Add($"Contract document missing on disk: {a.FilePath}");
            }
            if (!files.Any(f => f.Name.StartsWith("04_Contract/"))) res.Warnings.Add($"No contract document attached to {h.ContractNo} (Contracts page).");

            var ledgerKind = InvoiceKinds.IsSubcontractor(h);   // [phase6] supplier / owner MOS invoices have no room ledger
            if (!ledgerKind) res.Warnings.Add($"{InvoiceKinds.Of(h)} invoice: no room ledger, WIRs, site statements, tracker or checks report in this package.");
            // WIRs referenced by this invoice's ledger lines
            var lines = !ledgerKind ? new List<ClaimLine>() : LedgerRules.Effective(s.Claims.Where(c => c.Subcontractor.Equals(h.Subcontractor, StringComparison.OrdinalIgnoreCase) && c.InvoiceNo <= h.InvoiceNo))
                .Where(c => c.InvoiceNo == h.InvoiceNo || (c.IsCumulative && !c.ReplacedBySplit)).ToList();
            var wirNos = lines.SelectMany(c => Regex.Split(c.WirNo ?? "", @"[;,/\s]+")).Where(w => w.Trim().Length > 0).Select(w => w.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(w => w, StringComparer.OrdinalIgnoreCase).ToList();
            var pdfs = Directory.Exists(req.WirFolder) ? Directory.EnumerateFiles(req.WirFolder, "*.pdf", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).ToList() : new List<string>();
            if (wirNos.Count > 0 && !Directory.Exists(req.WirFolder)) res.Warnings.Add($"WIR folder not found: '{req.WirFolder}' (Settings).");
            foreach (var w in wirNos)
            {
                var key = PackageNames.NormalizeWir(w);
                var hit = req.RegisteredDocuments.Where(d => File.Exists(d.Path) && PackageNames.NormalizeWir(d.DocumentNo).Contains(key, StringComparison.Ordinal))
                              .Select(d => d.Path).OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault()
                          ?? pdfs.FirstOrDefault(p => PackageNames.NormalizeWir(Path.GetFileNameWithoutExtension(p)).Contains(key, StringComparison.Ordinal));
                if (hit is null) { res.MissingWirs.Add(w); continue; }
                files.Add(($"05_WIRs/{PackageNames.Safe(Path.GetFileName(hit))}", File.ReadAllBytes(hit), $"WIR {w}"));
            }

            var statementNos = !ledgerKind ? new HashSet<string>() : s.Statements.Where(st => st.Subcontractor.Equals(h.Subcontractor, StringComparison.OrdinalIgnoreCase)).Select(st => st.StatementNo).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var a in s.Attachments.Where(a => a.OwnerKind == AttachmentKinds.Statement && statementNos.Contains(a.OwnerKey)).OrderBy(a => a.OwnerKey).ThenBy(a => a.FileName))
                if (File.Exists(a.FilePath)) files.Add(($"06_Site_statements/{PackageNames.Safe(a.FileName)}", File.ReadAllBytes(a.FilePath), $"site statement {a.OwnerKey}"));

            if (ledgerKind)
            {
                var trackerName = $"07_Tracker_{PackageNames.Safe(h.Subcontractor)}_INV-{h.InvoiceNo}.xlsx";
                var tracker = Tmp(trackerName);
                TrackerExporter.Export(tracker, s, req.Plans, new TrackerExportScope { Subcontractor = h.Subcontractor, InvoiceNo = h.InvoiceNo, ContractNo = h.ContractNo, AsOf = stamp, MaxImageBytes = req.MaxPlanImageBytes }, b);
                files.Add((trackerName, File.ReadAllBytes(tracker), "head-office tracker (values, protected)"));

                var checks = Tmp("08_Checks.pdf");
                ChecksReport(checks, s, h, stamp);
                files.Add(("08_Checks.pdf", File.ReadAllBytes(checks), "height / length check decisions"));
            }
            foreach (var extra in req.ExtraFiles.Where(f => File.Exists(f.Path)))
                files.Add((extra.Name, File.ReadAllBytes(extra.Path), extra.Note));

            foreach (var f in files) res.Entries.Add(new PackageEntry(f.Name, f.Data.Length, Deterministic.Sha256(f.Data), f.Note));

            var index = Tmp("00_INDEX.pdf");
            IndexPdf(index, req, res, stamp, signed is not null);
            var indexBytes = File.ReadAllBytes(index);
            res.Entries.Insert(0, new PackageEntry("00_INDEX.pdf", indexBytes.Length, Deterministic.Sha256(indexBytes), "this index"));
            files.Insert(0, ("00_INDEX.pdf", indexBytes, ""));

            var when = new DateTimeOffset(stamp, TimeSpan.Zero);
            if (File.Exists(zipPath)) File.Delete(zipPath);
            using (var fs = new FileStream(zipPath, FileMode.CreateNew))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                foreach (var f in files)
                {
                    var e = zip.CreateEntry(f.Name, CompressionLevel.Optimal);
                    e.LastWriteTime = when;
                    using var w = e.Open();
                    w.Write(f.Data);
                }
            res.Sha256 = Deterministic.Sha256(zipPath);
            return res;
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { /* temp */ }
        }
    }

    private static string Ext(string name, string fallback) => Path.GetExtension(name) is { Length: > 0 } e ? e.ToLowerInvariant() : fallback;

    private static readonly string Red = "#8B0000";

    private static void IndexPdf(string path, PackageRequest req, PackageResult res, DateTime stamp, bool hasSigned)
    {
        var h = req.Build.Header;
        var t = req.Build.Totals;
        Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(32);
            page.DefaultTextStyle(x => x.FontSize(9));
            page.Header().Column(c =>
            {
                c.Item().Text($"{req.Info.ProjectName}").FontSize(16).Bold().FontColor(Red);
                c.Item().Text($"Invoice package  -  {res.Kind}").FontSize(12).Bold();
            });
            page.Content().PaddingTop(10).Column(c =>
            {
                c.Spacing(6);
                c.Item().Table(tb =>
                {
                    tb.ColumnsDefinition(cd => { cd.ConstantColumn(150); cd.RelativeColumn(); });
                    void Row(string k, string v) { tb.Cell().Text(k).Bold(); tb.Cell().Text(v); }
                    Row(InvoiceKinds.IsSubcontractor(h) ? "Subcontractor" : InvoiceKinds.Of(h) == InvoiceKinds.Supplier ? "Supplier" : "Invoice to owner", h.Subcontractor);
                    Row("Invoice kind", InvoiceKinds.Of(h));
                    Row(InvoiceKinds.Of(h) == InvoiceKinds.Supplier ? "PO no." : "Subcontract no.", h.ContractNo);
                    Row("Invoice", $"INV-{h.InvoiceNo:00}  Rev {h.Revision}  ({h.Status})");
                    Row("Period", h.PeriodTo is { } p ? $"to {p:dd MMM yyyy}" : req.Info.CoveredPeriod);
                    Row("Aconex workflow", string.IsNullOrEmpty(h.AconexWorkflowNo) ? "-" : h.AconexWorkflowNo);
                    Row("Subcontract value", $"SAR {t.SubcontractValue:N2}");
                    Row("Gross this period", $"SAR {t.CurrGross:N2}");
                    Row("Gross cumulative", $"SAR {t.CumGross:N2}");
                    Row($"Retention {t.RetentionPct:P0}", $"SAR {t.CurrRetention:N2}");
                    Row("Net incl. VAT 15 %", $"SAR {t.NetInclVatCurr:N2}");
                    Row("Prepared", $"{stamp:dd MMM yyyy}");
                    Row("Signed invoice", hasSigned ? "attached (03)" : "NOT ATTACHED - draft package");
                });
                c.Item().PaddingTop(8).Text("Contents").FontSize(11).Bold();
                c.Item().Table(tb =>
                {
                    tb.ColumnsDefinition(cd => { cd.RelativeColumn(3); cd.ConstantColumn(60); cd.RelativeColumn(4); });
                    tb.Header(hd =>
                    {
                        foreach (var x in new[] { "FILE", "KB", "SHA-256" }) hd.Cell().Background("#A6A6A6").Padding(2).Text(x).Bold();
                    });
                    foreach (var e in res.Entries)
                    {
                        tb.Cell().Padding(2).Text(e.Name);
                        tb.Cell().Padding(2).AlignRight().Text($"{e.Bytes / 1024.0:N0}");
                        tb.Cell().Padding(2).Text(e.Sha256).FontSize(6);
                    }
                    foreach (var w in res.MissingWirs)
                    {
                        tb.Cell().Padding(2).Text($"05_WIRs/{w}").FontColor(Red);
                        tb.Cell().Padding(2).AlignRight().Text("-");
                        tb.Cell().Padding(2).Text("MISSING").Bold().FontColor(Red);
                    }
                });
                if (res.Warnings.Count > 0)
                {
                    c.Item().PaddingTop(6).Text("Notes").Bold();
                    foreach (var w in res.Warnings) c.Item().Text("- " + w);
                }
                c.Item().PaddingTop(6).Text("The SHA-256 of 00_INDEX.pdf and of the ZIP itself are recorded on the invoice revision in Raffaello.").Italic().FontColor(Colors.Grey.Darken1);
            });
            page.Footer().AlignRight().Text(x => { x.Span("Page "); x.CurrentPageNumber(); x.Span(" / "); x.TotalPages(); });
        })).WithMetadata(Meta($"{h.Title} - package index", stamp)).GeneratePdf(path);
    }

    private static DocumentMetadata Meta(string title, DateTime stamp) =>
        new() { Title = title, Author = "Raffaello", Creator = "Raffaello", Producer = "Raffaello", CreationDate = stamp, ModifiedDate = stamp };

    /// <summary>Height (&gt;4.5 m) and length (15 m) decisions on the subcontractor's lines up to this invoice.</summary>
    public static void ChecksReport(string path, ProjectSnapshot s, SubInvoice h, DateTime stamp)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var lines = s.Claims.Where(c => c.Subcontractor.Equals(h.Subcontractor, StringComparison.OrdinalIgnoreCase) && c.InvoiceNo <= h.InvoiceNo && (c.QtyAbove45 != 0 || c.LengthApplies))
            .OrderBy(c => c.InvoiceNo).ThenBy(c => c.Room).ThenBy(c => c.Stage).ThenBy(c => c.Item).ToList();
        Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A4.Landscape());
            page.Margin(24);
            page.DefaultTextStyle(x => x.FontSize(7.5f));
            page.Header().Text($"{h.Subcontractor} INV-{h.InvoiceNo:00} Rev {h.Revision}  -  height and length checks").FontSize(13).Bold().FontColor(Red);
            page.Content().PaddingTop(8).Column(col =>
            {
                if (lines.Count == 0) { col.Item().Text("No height (>4.5 m) or length (15 m) claims up to this invoice."); return; }
                col.Item().Table(tb =>
                {
                    tb.ColumnsDefinition(cd => { cd.ConstantColumn(34); cd.RelativeColumn(1.4f); cd.RelativeColumn(1.2f); cd.RelativeColumn(1.4f); cd.ConstantColumn(46); cd.ConstantColumn(40);
                        cd.RelativeColumn(1.6f); cd.ConstantColumn(54); cd.RelativeColumn(1.4f); cd.RelativeColumn(4); });
                    tb.Header(hd =>
                    {
                        foreach (var x in new[] { "INV", "ROOM", "STAGE", "ITEM", "QTY", "CHECK", "CLAIMED / ACCEPTED", "STATUS", "BY / DATE", "NOTE" })
                            hd.Cell().Background("#A6A6A6").Padding(2).Text(x).Bold();
                    });
                    foreach (var c in lines)
                    {
                        void Row(string kind, string claimed, string status, string by, string note)
                        {
                            tb.Cell().Padding(2).Text(CumulativeSplit.InvoiceLabel(c));
                            tb.Cell().Padding(2).Text(c.Room);
                            tb.Cell().Padding(2).Text(c.Stage);
                            tb.Cell().Padding(2).Text(c.Item);
                            tb.Cell().Padding(2).AlignRight().Text($"{c.Qty:0.##}");
                            tb.Cell().Padding(2).Text(kind);
                            tb.Cell().Padding(2).Text(claimed);
                            tb.Cell().Padding(2).Text(status.Length == 0 ? "PENDING" : status).Bold().FontColor(status is "" or CheckStatus.Pending ? Red : Colors.Black);
                            tb.Cell().Padding(2).Text(by);
                            tb.Cell().Padding(2).Text(note);
                        }
                        if (c.QtyAbove45 != 0)
                            Row(">4.5 M", $"{c.QtyAbove45:0.##} / {HeightCheck.AcceptedHigh(c):0.##}", c.HeightStatus, $"{c.HeightCheckedBy} {c.HeightCheckDate:dd-MMM-yy}", c.HeightNote);
                        if (c.LengthApplies)
                            Row("15 M", $"{c.LengthClaimedQty:0.##} / {LengthCheck.InvoiceBaseQty(c):0.##}", c.LengthStatus, $"{c.LengthCheckedBy} {c.LengthCheckDate:dd-MMM-yy}", c.LengthNote);
                    }
                });
            });
            page.Footer().AlignRight().Text(x => { x.Span($"{stamp:dd MMM yyyy}   Page "); x.CurrentPageNumber(); x.Span(" / "); x.TotalPages(); });
        })).WithMetadata(Meta($"{h.Title} - checks", stamp)).GeneratePdf(path);
    }
}
