using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.App.Services;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Export;
using Raffaello.Core.Integrations.AconexApi;
using Raffaello.Core.Integrations.CadExchange;
using Raffaello.Core.Integrations.EPromise;
using Raffaello.Core.Portal;
using Raffaello.Core.Remote;
using Raffaello.Core.Statements;
using Raffaello.Core.Trust;
using Raffaello.Core.Variations;

namespace Raffaello.App.ViewModels;

public sealed class SignatureRow
{
    public required SignatureCheck Check { get; init; }
    public string Verdict => Check.Verdict;
    public string Status => Check.Ok ? "OK" : Check.Verdict == SignatureVerdicts.Changed ? "CHECK" : "OVER";
    public string Purpose => Check.Signature.Purpose;
    public string Kind => Check.Signature.Kind;
    public string Signer => $"{(Check.Signature.SignerName.Length > 0 ? Check.Signature.SignerName : Check.Signature.SignerUser)} ({Check.Signature.SignerRole})";
    public DateTime SignedAt => Check.Signature.SignedAt;
    public string Key => KeyFingerprint.Short(Check.Signature.KeyFingerprint);
    public string Message => Check.Message;
}

public sealed record SignableItem(string Table, long Id, string Title, SubInvoice? Invoice, Variation? Variation)
{
    public override string ToString() => Title;
}

public sealed class EPromisePreviewRow
{
    public string BoqCode { get; init; } = "";
    public string Description { get; init; } = "";
    public string CostCode { get; init; } = "";
    public string BudgetResourceCode { get; init; } = "";
    public double Qty { get; init; }
    public double Rate { get; init; }
    public double Amount { get; init; }
}

/// <summary>
/// [trust] TRUST &amp; INTEGRATIONS: audit integrity check, sign-off of invoice revisions / packages / variations with the
/// personal key, signed PDF copies and their verification, E-Promise export, CAD exchange import, the portal inbox (server mode)
/// and the Aconex API set-up.
/// </summary>
public sealed partial class TrustViewModel : PageViewModel
{
    public const string TabIntegrity = "INTEGRITY", TabSignatures = "SIGNATURES", TabEPromise = "E-PROMISE", TabCad = "CAD EXCHANGE", TabPortal = "PORTAL INBOX", TabAconex = "ACONEX API";

    public TrustViewModel(PageContext ctx) : base(ctx) { }

    public override string Key => "Trust";
    public override string Title => "TRUST & INTEGRATIONS";
    public override string Subtitle => "Tamper-evident audit, approvals with personal signatures, ERP / Aconex / CAD links, subcontractor portal inbox";
    public override bool ShowFilterBar => false;
    protected override bool UsesFilter => false;

    public string[] Tabs { get; } = { TabIntegrity, TabSignatures, TabEPromise, TabCad, TabPortal, TabAconex };
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsIntegrity), nameof(IsSignatures), nameof(IsEPromise), nameof(IsCad), nameof(IsPortal), nameof(IsAconex))]
    private string _tab = TabIntegrity;
    public bool IsIntegrity => Tab == TabIntegrity;
    public bool IsSignatures => Tab == TabSignatures;
    public bool IsEPromise => Tab == TabEPromise;
    public bool IsCad => Tab == TabCad;
    public bool IsPortal => Tab == TabPortal;
    public bool IsAconex => Tab == TabAconex;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";

    partial void OnTabChanged(string value) { if (IsActive) ForceRefresh(); }

    private RemoteProjectStore? Remote => Project.Store as RemoteProjectStore;
    public bool IsServerMode => Remote != null;

    private ITrustStore TrustStore => Project.Store switch
    {
        RemoteProjectStore r => new RemoteTrustStore(r),
        Db db => new SqliteTrustStore(db),
        _ => throw new InvalidOperationException("Signatures need the data file or the server."),
    };

    private static SigningKeyStore Keys() => SigningKeyStore.Default();

    private SignerInfo Signer()
    {
        if (Remote?.Me is { } me) return new SignerInfo(me.UserName, me.DisplayName, me.Role);
        var user = Project.Settings.EffectiveUserName;
        return new SignerInfo(user, user, "LOCAL");
    }

    protected override void Refresh()
    {
        OnPropertyChanged(nameof(IsServerMode));
        switch (Tab)
        {
            case TabSignatures: RefreshSignables(); RefreshKey(); break;
            case TabEPromise: RefreshInvoices(); break;
            case TabPortal: _ = RefreshPortalAsync(); break;
            case TabAconex: RefreshAconex(); break;
        }
    }

    private async Task Busy(string text, Func<Task> work)
    {
        if (IsBusy) return;
        IsBusy = true; BusyText = text;
        try { await work(); }
        catch (Exception ex) { Ctx.Toasts.Show(text.TrimEnd('.') + " FAILED", ex.Message, ToastKind.Error, 8); }
        finally { IsBusy = false; BusyText = ""; }
    }

    // ================================================================== integrity

    [ObservableProperty] private string _integritySummary = "Not checked yet.";
    [ObservableProperty] private bool _integrityOk;
    [ObservableProperty] private bool _independent;
    public ObservableCollection<string> IntegrityProblems { get; } = new();

    [RelayCommand]
    private Task VerifyIntegrity() => Busy("VERIFYING AUDIT LOG...", async () =>
    {
        var store = Project.Store;
        var independent = Independent;
        var rep = await Task.Run(() => new IntegrityChecker().Verify(store, independent));
        IntegritySummary = rep.Summary;
        IntegrityOk = rep.Ok;
        IntegrityProblems.Clear();
        foreach (var p in rep.Problems) IntegrityProblems.Add((p.IsWarning ? "warning  " : "PROBLEM  ") + p);
        Ctx.Toasts.Show(rep.Ok ? "AUDIT LOG INTACT" : "AUDIT LOG TAMPERED", rep.Summary, rep.Ok ? ToastKind.Good : ToastKind.Error, 10);
    });

    [RelayCommand]
    private void SaveIntegrityReport()
    {
        var path = Ctx.Dialogs.SaveFile("Integrity report", $"audit-integrity-{DateTime.Now:yyyyMMdd-HHmm}.txt", "Text|*.txt");
        if (path is null) return;
        File.WriteAllText(path, IntegritySummary + Environment.NewLine + string.Join(Environment.NewLine, IntegrityProblems));
        DialogService.OpenWithShell(path);
    }

    // ================================================================== signatures

    public ObservableCollection<SignableItem> Signables { get; } = new();
    public ObservableCollection<SignatureRow> Signatures { get; } = new();
    public string[] Purposes { get; } = SignaturePurposes.All;
    [ObservableProperty] private SignableItem? _selectedSignable;
    [ObservableProperty] private string _purpose = SignaturePurposes.Checked;
    [ObservableProperty] private string _keyText = "";

    partial void OnSelectedSignableChanged(SignableItem? value) => _ = VerifySelected();

    private void RefreshSignables()
    {
        var keep = SelectedSignable;
        Signables.Clear();
        foreach (var i in Project.Snapshot.SubInvoices.Where(InvoiceKinds.IsSubcontractor).OrderByDescending(i => i.InvoiceNo).ThenByDescending(i => i.Revision))
            Signables.Add(new SignableItem(SignedRecords.InvoiceTable, i.Id, $"{SignedRecords.InvoiceTitle(i)}  [{i.Status}]{(i.PackageSha256.Length > 0 ? "  + package" : "")}", i, null));
        try
        {
            foreach (var v in Project.Store.All<Variation>().OrderByDescending(v => v.Date))
                Signables.Add(new SignableItem(SignedRecords.VariationTable, v.Id, $"{v.Type} {v.Number} {v.Title}  [{v.Status}]", null, v));
        }
        catch (Exception) { /* no variation table in this data file yet */ }
        SelectedSignable = keep is null ? Signables.FirstOrDefault() : Signables.FirstOrDefault(s => s.Table == keep.Table && s.Id == keep.Id) ?? Signables.FirstOrDefault();
    }

    private void RefreshKey()
    {
        try
        {
            var me = Signer();
            var k = Keys().Info(me.User);
            if (k is null) { KeyText = $"{me.User}: no signing key yet - it is created (and registered) at the first signature."; return; }
            var registered = TrustStore.Keys().Any(r => r.Fingerprint == k.Fingerprint && r.RevokedAt is null);
            KeyText = $"{me.User} ({me.Role}) - key {k.ShortFingerprint}, created {k.CreatedAt:dd-MMM-yyyy} on {k.Machine}, protected by {k.Protector}" + (registered ? ", registered" : ", NOT registered yet");
        }
        catch (Exception ex) { KeyText = ex.Message; }
    }

    private TrustService Service() => new(Project.Store, TrustStore, Keys());

    [RelayCommand]
    private Task Sign() => SignCore(package: false);

    [RelayCommand]
    private Task SignPackage() => SignCore(package: true);

    private Task SignCore(bool package) => Busy("SIGNING...", async () =>
    {
        if (SelectedSignable is not { } item) { Ctx.Toasts.Show("PICK A RECORD", kind: ToastKind.Warn); return; }
        var signer = Signer();
        var purpose = Purpose;
        if (!Ctx.Dialogs.Confirm("Sign", $"Sign {item.Title}\nas {purpose} by {signer.DisplayName} ({signer.Role})?\n\nThe signature covers the content as it is now{(package ? " and the package file (SHA-256)" : "")}. Any later change shows as CHANGED.")) return;
        var sig = await Task.Run(() =>
        {
            var svc = Service();
            if (item.Variation is { } v) return svc.SignVariation(v, signer, purpose);
            var inv = item.Invoice!;
            return package ? svc.SignPackage(inv, signer, purpose) : svc.SignInvoice(inv, signer, purpose);
        });
        Ctx.Toasts.Show("SIGNED", $"{sig.RecordTitle}: {sig.Purpose} by {sig.SignerUser}, key {KeyFingerprint.Short(sig.KeyFingerprint)}", ToastKind.Good);
        RefreshKey();
        await VerifySelected();
    });

    [RelayCommand]
    private async Task VerifySelected()
    {
        Signatures.Clear();
        if (SelectedSignable is not { } item) return;
        try
        {
            var checks = await Task.Run(() => Service().Verify(item.Table, item.Id));
            foreach (var c in checks) Signatures.Add(new SignatureRow { Check = c });
        }
        catch (Exception ex) { Ctx.Toasts.Show("CANNOT VERIFY", ex.Message, ToastKind.Error); }
    }

    /// <summary>Signed copy of an exported PDF: approvals page appended + embedded digital signature with the user's key.</summary>
    [RelayCommand]
    private Task SignedPdfCopy() => Busy("MAKING SIGNED PDF...", async () =>
    {
        if (SelectedSignable is not { } item) { Ctx.Toasts.Show("PICK A RECORD", kind: ToastKind.Warn); return; }
        var src = Ctx.Dialogs.OpenFile("Exported PDF (invoice, package index, VO submission)", "PDF|*.pdf");
        if (src is null) return;
        var dest = Ctx.Dialogs.SaveFile("Signed copy", Path.GetFileNameWithoutExtension(src) + "_SIGNED.pdf", "PDF|*.pdf");
        if (dest is null) return;
        var signer = Signer();
        var checks = Signatures.Select(s => s.Check).ToList();
        var embed = Ctx.Dialogs.Confirm("Digital signature", "Also embed a digital signature with your key (PAdES-style, self-issued certificate)?\n\nNo = approvals page only.");
        await Task.Run(() =>
        {
            System.Security.Cryptography.X509Certificates.X509Certificate2? cert = null;
            try
            {
                if (embed)
                {
                    var keys = Keys();
                    keys.GetOrCreate(signer.User);
                    using var k = keys.OpenPrivateKey(signer.User);
                    cert = PdfCmsSigner.SelfIssuedCertificate(k, signer.DisplayName, signer.Role);
                }
                var audit = new IntegrityChecker().Verify(Project.Store).Summary;
                var pkg = item.Invoice?.PackageSha256;
                var bytes = SignedPdfExporter.Produce(File.ReadAllBytes(src), item.Title, checks, pkg, cert,
                    new PdfSignOptions { SignerName = signer.DisplayName, Reason = $"{Purpose} - {item.Title}", Location = "Riyadh" }, audit);
                File.WriteAllBytes(dest, bytes);
            }
            finally { cert?.Dispose(); }
        });
        Ctx.Toasts.Show("SIGNED PDF SAVED", dest, ToastKind.Good);
        DialogService.OpenWithShell(dest);
    });

    [ObservableProperty] private string _pdfCheckText = "";

    [RelayCommand]
    private async Task VerifyPdf()
    {
        var src = Ctx.Dialogs.OpenFile("PDF to verify", "PDF|*.pdf");
        if (src is null) return;
        var sigs = await Task.Run(() => PdfSignatureVerifier.Verify(File.ReadAllBytes(src)));
        PdfCheckText = sigs.Count == 0 ? $"{Path.GetFileName(src)}: no embedded signature." : $"{Path.GetFileName(src)}:\n" + string.Join("\n", sigs.Select(s => "  " + s.Summary));
        Ctx.Toasts.Show(sigs.Count > 0 && sigs[^1].Ok ? "PDF SIGNATURE VALID" : "PDF NOT VALID", PdfCheckText, sigs.Count > 0 && sigs[^1].Ok ? ToastKind.Good : ToastKind.Warn, 10);
    }

    [RelayCommand]
    private async Task VerifyPackageZip()
    {
        var zip = Ctx.Dialogs.OpenFile("Invoice package (ZIP)", "ZIP|*.zip");
        if (zip is null) return;
        var checks = await Task.Run(() => Service().VerifyPackageFile(zip));
        PdfCheckText = checks.Count == 0 ? $"{Path.GetFileName(zip)}: NO approval covers these exact bytes."
            : $"{Path.GetFileName(zip)}:\n" + string.Join("\n", checks.Select(c => $"  {c.Verdict}: {c.Message}"));
        Ctx.Toasts.Show(checks.Count > 0 && checks.All(c => c.Ok) ? "PACKAGE APPROVED AS IS" : "PACKAGE NOT COVERED", PdfCheckText, checks.Count > 0 && checks.All(c => c.Ok) ? ToastKind.Good : ToastKind.Warn, 10);
    }

    // ================================================================== E-Promise

    public ObservableCollection<SubInvoice> Invoices { get; } = new();
    public ObservableCollection<EPromisePreviewRow> EPromiseRows { get; } = new();
    public ObservableCollection<string> EPromiseIssues { get; } = new();
    [ObservableProperty] private SubInvoice? _epInvoice;
    [ObservableProperty] private bool _epForce;
    [ObservableProperty] private string _epSummary = "";
    private EPromiseExportResult? _ep;

    private void RefreshInvoices()
    {
        var keep = EpInvoice?.Id;
        Invoices.Clear();
        foreach (var i in Project.Snapshot.SubInvoices.Where(InvoiceKinds.IsSubcontractor).OrderByDescending(i => EPromiseExporter.IsCertified(i)).ThenByDescending(i => i.InvoiceNo).ThenByDescending(i => i.Revision))
            Invoices.Add(i);
        EpInvoice = Invoices.FirstOrDefault(i => i.Id == keep) ?? Invoices.FirstOrDefault();
    }

    partial void OnEpInvoiceChanged(SubInvoice? value) => BuildEPromise();

    [RelayCommand]
    private void BuildEPromise()
    {
        EPromiseRows.Clear(); EPromiseIssues.Clear(); _ep = null; EpSummary = "";
        if (EpInvoice is not { } inv) return;
        try
        {
            var cfg = EPromiseExportConfig.Load();
            foreach (var p in cfg.Validate()) EPromiseIssues.Add("CONFIG: " + p);
            _ep = EPromiseExporter.Build(Project.Snapshot, inv, cfg, EpForce);
            foreach (var r in _ep.Rows)
                EPromiseRows.Add(new EPromisePreviewRow
                {
                    BoqCode = r[EPromiseFields.BoqCode] as string ?? "", Description = r[EPromiseFields.BoqDescription] as string ?? "", CostCode = r[EPromiseFields.CostCode] as string ?? "",
                    BudgetResourceCode = r[EPromiseFields.BudgetResourceCode] as string ?? "", Qty = r.Qty, Rate = r[EPromiseFields.Rate] is double d ? d : 0, Amount = r.Amount,
                });
            foreach (var i in _ep.Issues) EPromiseIssues.Add(i);
            EpSummary = _ep.Summary;
        }
        catch (Exception ex) { EpSummary = ex.Message; }
    }

    partial void OnEpForceChanged(bool value) => BuildEPromise();

    [RelayCommand]
    private void ExportEPromise(string? format)
    {
        if (_ep is null || _ep.Blocked) { Ctx.Toasts.Show("NOT EXPORTED", _ep?.Issues.FirstOrDefault() ?? "Pick a certified invoice revision.", ToastKind.Warn); return; }
        var csv = string.Equals(format, "CSV", StringComparison.OrdinalIgnoreCase);
        var name = $"EPROMISE_{_ep.Invoice.ContractNo}_INV-{_ep.Invoice.InvoiceNo:00}_Rev{_ep.Invoice.Revision}{(csv ? ".csv" : ".xlsx")}";
        var path = Ctx.Dialogs.SaveFile("E-Promise import file", name, csv ? "CSV|*.csv" : "Excel workbook|*.xlsx");
        if (path is null) return;
        var cfg = EPromiseExportConfig.Load();
        if (csv) EPromiseExporter.WriteCsv(path, _ep, cfg); else EPromiseExporter.WriteXlsx(path, _ep, cfg);
        Project.Store.LogEvent("EXPORT", $"E-Promise export {name}: {_ep.Rows.Count} rows, {_ep.TotalAmount:N2}");
        Ctx.Toasts.Show("E-PROMISE FILE SAVED", path, ToastKind.Good);
        DialogService.OpenWithShell(path);
    }

    [RelayCommand]
    private void OpenEPromiseMapping()
    {
        EPromiseExportConfig.Load();   // writes the defaults the first time
        DialogService.OpenWithShell(EPromiseExportConfig.DefaultPath);
    }

    // ================================================================== CAD exchange

    public ObservableCollection<CadQtyChange> CadChanges { get; } = new();
    public ObservableCollection<string> CadMessages { get; } = new();
    public string[] CadModes { get; } = { CadImportModes.Replace, CadImportModes.AddMissing };
    [ObservableProperty] private string _cadMode = CadImportModes.Replace;
    [ObservableProperty] private string _cadStages = "1ST FIX, 2ND FIX";
    [ObservableProperty] private string _cadSummary = "Open a .json file written by the AutoCAD / Revit add-in or the Dynamo script.";
    [ObservableProperty] private bool _cadCanImport;
    private CadImportPreview? _cad;

    [RelayCommand]
    private async Task OpenCadFile()
    {
        var path = Ctx.Dialogs.OpenFile("CAD exchange file", "Raffaello CAD exchange|*.json|All files|*.*");
        if (path is null) return;
        var opt = new CadImportOptions
        {
            Building = WorkingBuilding, Mode = CadMode,
            Stages = CadStages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        };
        _cad = await Task.Run(() => CadExchangeImporter.Preview(path, Project.Snapshot, opt));
        CadChanges.Clear(); CadMessages.Clear();
        foreach (var e in _cad.Errors) CadMessages.Add("ERROR  " + e);
        foreach (var w in _cad.Warnings) CadMessages.Add("warning  " + w);
        foreach (var q in _cad.QtyChanges) CadChanges.Add(q);
        CadSummary = _cad.Summary;
        CadCanImport = _cad.CanCommit;
    }

    [RelayCommand]
    private async Task ImportCad()
    {
        if (_cad is null || !_cad.CanCommit) return;
        var changes = _cad.QtyChanges.Count(q => q.Action is "ADD" or "UPDATE");
        if (!Ctx.Dialogs.Confirm("CAD import", $"{_cad.Summary}\n\nWrite {_cad.NewRooms.Count} new rooms, {_cad.Shapes.Count} room shapes and {changes} PROJECT QTY changes?")) return;
        var p = _cad;
        var msg = "";
        if (await Ctx.Data.WriteAsync(proj => { msg = CadExchangeImporter.Commit(p, proj.Store); proj.Reload(); }, Ctx.Toasts))
        {
            Ctx.Toasts.Show("CAD IMPORTED", msg, ToastKind.Good);
            _cad = null; CadCanImport = false; CadChanges.Clear(); CadMessages.Clear(); CadSummary = msg;
        }
    }

    [RelayCommand]
    private void SaveCadSchema()
    {
        var path = Ctx.Dialogs.SaveFile("CAD exchange JSON schema", "raffaello-cad-exchange.schema.json", "JSON|*.json");
        if (path is null) return;
        File.WriteAllText(path, CadExchangeImporter.JsonSchema());
        var map = Path.Combine(Path.GetDirectoryName(path)!, "blockmap.json");
        if (!File.Exists(map)) CadExchangeJson.WriteBlockMap(map, BlockMapping.Default());
        Ctx.Toasts.Show("SCHEMA SAVED", $"{path} (+ blockmap.json starting rules for the add-in)", ToastKind.Good);
    }

    // ================================================================== portal inbox

    public ObservableCollection<PortalSubmission> PortalSubmissions { get; } = new();
    public ObservableCollection<DocumentInfo> PortalFiles { get; } = new();
    public ObservableCollection<StatementPreviewRow> PortalPreview { get; } = new();
    public ObservableCollection<PortalMessage> PortalMessages { get; } = new();
    public ObservableCollection<PortalCompanySetting> PortalCompanies { get; } = new();
    public ObservableCollection<PortalAccountDto> PortalAccounts { get; } = new();
    [ObservableProperty] private PortalSubmission? _selectedSubmission;
    [ObservableProperty] private bool _openOnly = true;
    [ObservableProperty] private string _portalInvoiceNo = "1";
    [ObservableProperty] private string _portalOverReason = "";
    [ObservableProperty] private string _portalReason = "";
    [ObservableProperty] private string _portalMessage = "";
    [ObservableProperty] private string _portalPreviewText = "";
    [ObservableProperty] private string _newCompany = "";
    [ObservableProperty] private string _newCompanyScope = "";
    [ObservableProperty] private string _newAccountUser = "";
    [ObservableProperty] private string _newAccountPassword = "";
    private StatementImportResult? _portalPreview;
    private string _portalPreviewPath = "";

    private PortalInbox? Inbox => Remote is { } r ? new PortalInbox(r) : null;
    private static string InboxFolder => Path.Combine(Path.GetTempPath(), "Raffaello", "portal-inbox");

    partial void OnOpenOnlyChanged(bool value) => _ = RefreshPortalAsync();

    partial void OnSelectedSubmissionChanged(PortalSubmission? value)
    {
        PortalFiles.Clear(); PortalPreview.Clear(); PortalPreviewText = ""; _portalPreview = null;
        PortalMessages.Clear();
        if (value is null || Inbox is not { } inbox) return;
        _ = Task.Run(() => (inbox.Files(value), inbox.Messages(value.Company))).ContinueWith(t =>
        {
            if (t.IsFaulted) return;
            foreach (var f in t.Result.Item1) PortalFiles.Add(f);
            foreach (var m in t.Result.Item2.TakeLast(30)) PortalMessages.Add(m);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private async Task RefreshPortalAsync()
    {
        if (Inbox is not { } inbox) { PortalSubmissions.Clear(); return; }
        var openOnly = OpenOnly;
        try
        {
            var (subs, companies) = await Task.Run(() => (inbox.Submissions(openOnly), inbox.Companies()));
            var keep = SelectedSubmission?.Id;
            PortalSubmissions.Clear();
            foreach (var s in subs) PortalSubmissions.Add(s);
            PortalCompanies.Clear();
            foreach (var c in companies) PortalCompanies.Add(c);
            SelectedSubmission = PortalSubmissions.FirstOrDefault(s => s.Id == keep) ?? PortalSubmissions.FirstOrDefault();
            try
            {
                var accounts = await Task.Run(inbox.Accounts);
                PortalAccounts.Clear();
                foreach (var a in accounts) PortalAccounts.Add(a);
            }
            catch (PermissionDeniedException) { PortalAccounts.Clear(); }
        }
        catch (Exception ex) { Ctx.Toasts.Show("PORTAL INBOX", ex.Message, ToastKind.Warn); }
    }

    [RelayCommand]
    private Task RefreshPortal() => RefreshPortalAsync();

    [RelayCommand]
    private Task OpenSubmissionFiles() => Busy("DOWNLOADING...", async () =>
    {
        if (SelectedSubmission is not { } s || Inbox is not { } inbox) return;
        var folder = Path.Combine(InboxFolder, $"{s.Company}_{s.Id}");
        await Task.Run(() => inbox.Download(s, folder));
        if (s.Status == PortalSubmissionStatus.Submitted) await Task.Run(() => inbox.MarkUnderReview(s));
        DialogService.OpenWithShell(folder);
        await RefreshPortalAsync();
    });

    [RelayCommand]
    private Task PreviewSubmission() => Busy("READING STATEMENT...", async () =>
    {
        if (SelectedSubmission is not { } s || Inbox is not { } inbox) return;
        var inv = int.TryParse(PortalInvoiceNo, out var n) ? n : 0;
        var building = PortalCompanies.FirstOrDefault(c => c.Name.Equals(s.Company, StringComparison.OrdinalIgnoreCase))?.Building ?? WorkingBuilding;
        var (res, path) = await Task.Run(() => inbox.PreviewStatement(s, Project.Snapshot, inv, building, Path.Combine(InboxFolder, $"{s.Company}_{s.Id}")));
        _portalPreview = res; _portalPreviewPath = path;
        PortalPreview.Clear();
        foreach (var (line, check) in res.Checks) PortalPreview.Add(new StatementPreviewRow { Line = line, Check = check });
        PortalPreviewText = res.Summary + (res.Issues.Count > 0 ? " - " + string.Join("; ", res.Issues.Take(3).Select(i => i.Message)) : "");
    });

    [RelayCommand]
    private async Task ImportSubmission()
    {
        if (SelectedSubmission is not { } s || Inbox is not { } inbox || _portalPreview is null) { Ctx.Toasts.Show("PREVIEW THE STATEMENT FIRST", kind: ToastKind.Warn); return; }
        if (_portalPreview.IsDuplicate) { Ctx.Toasts.Show("DUPLICATE - REJECT IT INSTEAD", kind: ToastKind.Warn); return; }
        var inv = int.TryParse(PortalInvoiceNo, out var n) ? n : 0;
        var reason = string.IsNullOrWhiteSpace(PortalOverReason) ? null : PortalOverReason.Trim();
        if (_portalPreview.Blocked > 0 && reason is null && !Ctx.Dialogs.Confirm("Over remaining", $"{_portalPreview.Blocked} line(s) are above the remaining quantity and will be SKIPPED. Continue?")) return;
        var (res, path) = (_portalPreview, _portalPreviewPath);
        var posted = 0;
        if (await Ctx.Data.WriteAsync(p => { posted = inbox.Import(s, res, path, inv, reason); p.Reload(); }, Ctx.Toasts))
        {
            Ctx.Toasts.Show("STATEMENT POSTED", $"{posted} claim lines from {s.Company}; the subcontractor was told", ToastKind.Good);
            _portalPreview = null; PortalPreview.Clear(); PortalPreviewText = "";
            await RefreshPortalAsync();
        }
    }

    [RelayCommand]
    private async Task RejectSubmission()
    {
        if (SelectedSubmission is not { } s || Inbox is not { } inbox) return;
        if (string.IsNullOrWhiteSpace(PortalReason)) { Ctx.Toasts.Show("TYPE THE REASON", "The subcontractor sees it on the portal.", ToastKind.Warn); return; }
        var reason = PortalReason.Trim();
        if (await Ctx.Data.WriteAsync(_ => inbox.Reject(s, reason), Ctx.Toasts, "SUBMISSION REJECTED")) { PortalReason = ""; await RefreshPortalAsync(); }
    }

    [RelayCommand]
    private async Task SendPortalMessage()
    {
        if (SelectedSubmission is not { } s || Inbox is not { } inbox || string.IsNullOrWhiteSpace(PortalMessage)) return;
        var text = PortalMessage.Trim();
        if (await Ctx.Data.WriteAsync(_ => inbox.Send(s.Company, $"About {(s.StatementNo.Length > 0 ? s.StatementNo : "#" + s.Id)}", text, s.Id), Ctx.Toasts, "MESSAGE SENT"))
        {
            PortalMessage = "";
            OnSelectedSubmissionChanged(s);
        }
    }

    [RelayCommand]
    private async Task AddCompany()
    {
        if (Inbox is not { } inbox || string.IsNullOrWhiteSpace(NewCompany)) return;
        var c = new PortalCompanySetting { Name = NewCompany.Trim(), DisplayName = NewCompany.Trim(), Building = WorkingBuilding, RoomScope = NewCompanyScope.Trim() };
        if (await Ctx.Data.WriteAsync(_ => inbox.SaveCompany(c), Ctx.Toasts, "PORTAL COMPANY SAVED")) { NewCompany = ""; NewCompanyScope = ""; await RefreshPortalAsync(); }
    }

    [RelayCommand]
    private async Task AddAccount()
    {
        if (Inbox is not { } inbox) return;
        var company = PortalCompanies.FirstOrDefault()?.Name;
        if (SelectedSubmission != null) company = SelectedSubmission.Company;
        if (string.IsNullOrWhiteSpace(NewAccountUser) || string.IsNullOrWhiteSpace(NewAccountPassword) || company is null)
        { Ctx.Toasts.Show("USER, PASSWORD AND A COMPANY ARE NEEDED", "Pick a submission of the company or add the company first.", ToastKind.Warn); return; }
        var dto = new PortalAccountDto { UserName = NewAccountUser.Trim(), DisplayName = NewAccountUser.Trim(), Company = company, Password = NewAccountPassword };
        if (await Ctx.Data.WriteAsync(_ => inbox.SaveAccount(dto), Ctx.Toasts, $"PORTAL ACCOUNT CREATED FOR {company}")) { NewAccountUser = ""; NewAccountPassword = ""; await RefreshPortalAsync(); }
    }

    [RelayCommand]
    private void OpenPortalPage()
    {
        if (Remote is { } r) DialogService.OpenWithShell(r.Location.TrimEnd('/') + PortalRoutes.Page);
    }

    // ================================================================== Aconex API

    [ObservableProperty] private string _aconexApiStatus = "";
    [ObservableProperty] private string _aconexSecret = "";

    private void RefreshAconex()
    {
        var cfg = AconexApiConfig.LoadSafe();
        var problems = cfg.Validate();
        AconexApiStatus = (cfg.Enabled ? "ENABLED - workflow lookups and downloads use the Aconex API." : "OFF - the browser automation is used (set Enabled: true in aconex-api.json when MOBCO has API access).")
                          + $"\n{AconexApiConfig.DefaultPath}\nProject {(cfg.ProjectId.Length > 0 ? cfg.ProjectId : "-")}, auth {cfg.AuthMode}, secret {(cfg.SecretProtected.Length > 0 ? "stored (DPAPI)" : "not stored")}"
                          + (problems.Count > 0 ? "\n" + string.Join("\n", problems.Select(p => "! " + p)) : "");
    }

    [RelayCommand]
    private void OpenAconexApiConfig()
    {
        if (!File.Exists(AconexApiConfig.DefaultPath)) AconexApiConfig.Load();
        DialogService.OpenWithShell(AconexApiConfig.DefaultPath);
    }

    [RelayCommand]
    private void SaveAconexSecret()
    {
        if (string.IsNullOrEmpty(AconexSecret)) return;
        var cfg = AconexApiConfig.Load();
        cfg.SetSecret(AconexSecret);
        cfg.Save();
        AconexSecret = "";
        RefreshAconex();
        Ctx.Toasts.Show("SECRET STORED", "Encrypted for your Windows account (DPAPI).", ToastKind.Good);
    }

    [RelayCommand]
    private Task TestAconexApi() => Busy("CONNECTING TO ACONEX API...", async () =>
    {
        var cfg = AconexApiConfig.Load();
        await using var c = new AconexApiClient(cfg);
        var projects = await c.ProjectsAsync();
        await c.EnsureLoggedInAsync();
        AconexApiStatus += $"\nOK - {projects.Count} project(s): " + string.Join(", ", projects.Select(p => $"{p.ProjectId} {p.Name}"));
        Ctx.Toasts.Show("ACONEX API OK", $"{projects.Count} project(s) visible", ToastKind.Good);
    });

    public override IEnumerable<ExportSheet> ExportCurrentView()
    {
        if (IsSignatures)
            yield return new ExportSheet
            {
                Name = "SIGNATURES", Title = SelectedSignable?.Title,
                Columns = new() { new("VERDICT"), new("PURPOSE"), new("KIND"), new("SIGNER", Width: 30), new("SIGNED", ColumnKind.Date), new("KEY"), new("MESSAGE", Width: 80) },
                Rows = Signatures.Select(s => new object?[] { s.Verdict, s.Purpose, s.Kind, s.Signer, s.SignedAt, s.Key, s.Message }).ToList(),
            };
        if (IsEPromise)
            yield return new ExportSheet
            {
                Name = "E-PROMISE", Title = EpSummary,
                Columns = new() { new("BOQ CODE"), new("DESCRIPTION", Width: 50), new("COST CODE"), new("BUDGET RESOURCE"), new("QTY", ColumnKind.Number), new("RATE", ColumnKind.Money), new("AMOUNT", ColumnKind.Money) },
                Rows = EPromiseRows.Select(r => new object?[] { r.BoqCode, r.Description, r.CostCode, r.BudgetResourceCode, r.Qty, r.Rate, r.Amount }).ToList(),
            };
    }
}
