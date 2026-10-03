using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.Core.Domain;
using Raffaello.Core.Ledger;
using Raffaello.Core.Queue;
using Raffaello.Core.Tracker;

namespace Raffaello.App.ViewModels;

/// <summary>A page whose sub-screens are picked by a Tab string (hubs, TRACKING): the HOME lists can open a sub-screen directly.</summary>
public interface ITabbedPage
{
    string Tab { get; set; }
}

public sealed partial class MaterialsHubViewModel : ITabbedPage { }
public sealed partial class AconexHubViewModel : ITabbedPage { }
public sealed partial class OwnerMosViewModel : ITabbedPage { }

/// <summary>One item in an area's list (level 2): a page, optionally one of its tabs. Also the side bar entry while inside the area.</summary>
public sealed partial class AreaLink : ObservableObject
{
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string PageKey { get; init; }
    public string? Tab { get; init; }
    public string Icon { get; init; } = "IconArrowRight";
    [ObservableProperty] private string _count = "";
    /// <summary>The page shown now (side bar highlight). Setting it from the side bar opens the item.</summary>
    [ObservableProperty] private bool _isCurrent;
    public Action<AreaLink>? OnSelected { get; set; }
    partial void OnIsCurrentChanged(bool value) { if (value) OnSelected?.Invoke(this); }
}

/// <summary>HOME key figure (one compact card); click opens the tracker view.</summary>
public sealed record HomeFigure(string Building, string Label, string Value, string Detail, string Tab, string Status = "");

/// <summary>HOME "needs attention" / "recent" line; click opens the page (and tab).</summary>
public sealed record HomeLine(string Title, string Count, string Detail, string PageKey, string? Tab, string Status = "");

/// <summary>A HOME tile (level 1): a work area with a live one-line status and the list of its items.</summary>
public sealed partial class AreaTile : ObservableObject
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Icon { get; init; }
    public required string Description { get; init; }
    public bool IsMain { get; init; } = true;
    public IReadOnlyList<AreaLink> Links { get; init; } = Array.Empty<AreaLink>();
    /// <summary>Colourful area app icon (Themes/AreaIcons.xaml, key AreaIcon + Key).</summary>
    public string AppIcon => "AreaIcon" + Key;
    /// <summary>"What's inside" (tooltip on the tile).</summary>
    public string Inside => string.Join("  |  ", Links.Select(l => l.Title));
    [ObservableProperty] private string _status = "";
}

/// <summary>
/// HOME. Level 1: big tiles for the main work areas (+ More). Level 2: the plain list of what is inside the chosen area
/// (name, one-line description, count). Level 3 is the page itself; the header shows Home &gt; Area &gt; Page.
/// Every page of the app is in exactly one list (Settings is the side bar button); docs: UI_HOME_TRACKER_NOTES.md.
/// </summary>
public sealed partial class HomeViewModel : PageViewModel
{
    public const string MoreKey = "MORE";

    private static readonly Dictionary<string, string> PageIcons = new()
    {
        ["Tracking"] = "IconLedger", ["Checks"] = "IconChecks", ["SiteStatements"] = "IconSend", ["Statements"] = "IconStatements", ["Invoices"] = "IconInvoices",
        ["Materials"] = "IconMaterials", ["MaterialRecon"] = "IconMaterials", ["Aconex"] = "IconAconex", ["Wir"] = "IconWir", ["OwnerMos"] = "IconExport",
        ["Variations"] = "IconContracts", ["Boq"] = "IconDatabase", ["EarnedValue"] = "IconDashboard", ["CashFlow"] = "IconReports", ["Reports"] = "IconReports",
        ["Trust"] = "IconChecks", ["Brief"] = "IconClock", ["Ledger"] = "IconLedger", ["Plan"] = "IconPlan", ["Quantities"] = "IconQuantities", ["Welcome"] = "IconHome",
        ["Dashboard"] = "IconDashboard", ["Anomalies"] = "IconAlert", ["RateBenchmark"] = "IconContracts", ["Contracts"] = "IconContracts", ["Assemblies"] = "IconQuantities",
        ["Drawings"] = "IconPlan", ["Cables"] = "IconLink", ["Assistant"] = "IconSparkles",
    };

    public HomeViewModel(PageContext ctx) : base(ctx)
    {
        AreaLink L(string title, string desc, string page, string? tab = null) => new() { Title = title, Description = desc, PageKey = page, Tab = tab, Icon = PageIcons.GetValueOrDefault(page, "IconArrowRight") };
        Main = new[]
        {
            new AreaTile { Key = "SC", Title = "S/C Invoices", Icon = "IconInvoices", Description = "Subcontractor statements, checks, rates and invoices",
                Links = new[]
                {
                    L("Statement entry", "Type a subcontractor statement per location: remaining before entry, post within remaining, comparison to send back", "Tracking", "ENTRY"),
                    L("Claim lines and amounts", "Every claim line with rate and amount (after site % / WIR % / stage %), totals per subcontractor / invoice / location", "Tracking", "LEDGER"),
                    L("Rates", "Contract rate per subcontractor x stage|item; NO RATE shown in yellow; set a rate or import the RECON_TOOLS rate mapping", "Tracking", "RATES"),
                    L("Invoice status", "Per subcontractor x invoice: drawing done / drawing pending / differs from invoice; mark pending / done", "Tracking", "INVOICES"),
                    L("Checks", "Height above 4.5 m and 15 m route-length checks waiting for a decision", "Checks"),
                    L("Site statements", "Issue a statement sheet per subcontractor and import it back into the ledger", "SiteStatements"),
                    L("Statements received", "Statements / invoices received from subcontractors and their status", "Statements"),
                    L("S/C invoices", "Build the subcontractor invoice from the ledger, approve, export Excel / PDF", "Invoices"),
                } },
            new AreaTile { Key = "MAT", Title = "Material Invoices", Icon = "IconMaterials", Description = "PO, delivery notes, MIR, supplier invoices",
                Links = new[]
                {
                    L("Purchase orders", "POs with lines, delivered value and coding", "Materials", "PURCHASE ORDERS"),
                    L("Delivery notes", "DNs against the POs", "Materials", "DELIVERY NOTES"),
                    L("MIR", "Material inspection requests and their DNs", "Materials", "MIR"),
                    L("Supplier invoices", "Supplier invoices and locked DNs", "Materials", "SUPPLIER INVOICES"),
                    L("3-way match", "PO = DN = invoice check", "Materials", "3-WAY MATCH"),
                    L("DN lookup", "Find a DN by number / material", "Materials", "DN LOOKUP"),
                    L("Overview", "Materials at a glance", "Materials", "OVERVIEW"),
                    L("Material recon", "Delivered vs installed vs norms", "MaterialRecon"),
                    L("Materials settings", "Coding memory and reading options", "Materials", "SETTINGS"),
                } },
            new AreaTile { Key = "ACONEX", Title = "Aconex", Icon = "IconAconex", Description = "Workflows, WIR / MIR, downloads",
                Links = new[]
                {
                    L("Status board", "Where each invoice / WIR is: workflow step, who, due", "Aconex", "STATUS BOARD"),
                    L("Workflow lookup", "Look up a workflow and its steps", "Aconex", "WORKFLOWS"),
                    L("Downloads", "Download documents from the register", "Aconex", "DOWNLOADS"),
                    L("WIR / MIR", "Inspection requests: open, overdue, linked lines", "Wir"),
                    L("Script runner", "Run saved Aconex scripts", "Aconex", "SCRIPT RUNNER"),
                    L("Aconex setup", "Sign-in, selectors, folders", "Aconex", "SETUP"),
                } },
            new AreaTile { Key = "OWNER", Title = "Owner", Icon = "IconExport", Description = "Owner MOS / IPC, variations, owner BOQ, cash",
                Links = new[]
                {
                    L("Owner MOS (App F)", "Materials on site valuation for the IPC", "OwnerMos"),
                    L("Variations / EI", "Register, suggestions, submission export", "Variations"),
                    L("Owner BOQ", "Owner BOQ items and codes", "Boq"),
                    L("Earned value", "Planned vs earned vs certified", "EarnedValue"),
                    L("Cash flow", "Payments in and out by month", "CashFlow"),
                } },
            new AreaTile { Key = "REPORTS", Title = "Reports", Icon = "IconReports", Description = "Weekly reports, exports, integrations",
                Links = new[]
                {
                    L("Reports", "Weekly progress workbook and the other exports", "Reports"),
                    L("Trust & integrations", "Signatures, audit chain, E-Promise / CAD exchange", "Trust"),
                    L("Morning brief", "What changed since yesterday, what needs you today", "Brief"),
                } },
            new AreaTile { Key = "TRACKING", Title = "Tracking", Icon = "IconLedger", Description = "The MEP tracking sheet, from the app data",
                Links = new[]
                {
                    L("Dashboard", "Progress per stage / system / subcontractor / part", "Tracking", "DASHBOARD"),
                    L("Control", "Over cap, no cap, unknown location, 15 m / height pending, not compared, missing rates, invoices that differ", "Tracking", "CONTROL"),
                    L("Project qty", "Locations x stage|item totals (the 100% quantity)", "Tracking", "PROJECT QTY"),
                    L("Rooms", "One row per location: total, all subcontractors, remaining, status", "Tracking", "ROOMS"),
                    L("Room", "One location: stage progress, subcontractors, every stage + item line", "Tracking", "ROOM"),
                    L("Plans", "Progress per plan sheet; open the plan with the rooms coloured", "Tracking", "PLANS"),
                    L("Summary", "Location | stage | item: project qty, each subcontractor, remaining", "Tracking", "SUMMARY"),
                    L("Cap", "The cap list (key, location, stage, item, project qty)", "Tracking", "CAP"),
                    L("Cables (not compared)", "Cable pulling / cable tray lines, not compared with a total", "Tracking", "CABLES"),
                    L("Rooms & ledger (plan)", "Room list with the plan picture, post / reverse a claim", "Ledger"),
                    L("Plan view", "Whole plans coloured by status", "Plan"),
                    L("Quantities", "QS chain lines (legacy quantities)", "Quantities"),
                } },
            new AreaTile { Key = "SUMMARY", Title = "Summary", Icon = "IconDashboard", Description = "Today, dashboards, anomalies",
                Links = new[]
                {
                    L("Today", "Greeting, since you were last here, needs you today, data file", "Welcome"),
                    L("Dashboard", "Needs you today, S-curve, heat map", "Dashboard"),
                    L("Anomalies", "Unusual claims, rates, quantities", "Anomalies"),
                    L("Rate benchmark", "Rates compared across contracts", "RateBenchmark"),
                } },
        };
        More = new[]
        {
            new AreaTile { Key = "CONTRACTS", Title = "Contracts & BOQ breakdown", Icon = "IconContracts", Description = "Subcontracts, rate schedules, BOQ links, item breakdown", IsMain = false,
                Links = new[] { L("Contracts", "Subcontracts, items, BOQ links, contract rules", "Contracts"), L("BOQ breakdown", "Components and built-up rate of an item", "Assemblies") } },
            new AreaTile { Key = "DRAWINGS", Title = "Drawings", Icon = "IconPlan", Description = "Takeoff from PDF, DWG, DXF, IFC; revision compare", IsMain = false,
                Links = new[] { L("Drawings", "Count symbols / measure lines; statement highlights; revision compare", "Drawings") } },
            new AreaTile { Key = "CABLES", Title = "Cables", Icon = "IconLink", Description = "Cable schedule, FROM-TO claims, flags", IsMain = false,
                Links = new[] { L("Cables", "SLD / cable schedule, claims, duplicate FROM-TO, measured lengths", "Cables") } },
            new AreaTile { Key = "ASSISTANT", Title = "Assistant", Icon = "IconSparkles", Description = "Ask Raffaello with history and sources", IsMain = false,
                Links = new[] { L("Ask Raffaello", "Chat with history, sources and confirm cards", "Assistant") } },
        };
        MoreTile = new AreaTile { Key = MoreKey, Title = "More", Icon = "IconCommand", Description = "Contracts & BOQ breakdown, drawings, cables, assistant", IsMain = false };
    }
    public override string Key => "Home";
    public override string Title => Area?.Title ?? "Home";
    public override string Subtitle => Area is null ? "Pick an area - then pick what you want to do inside it" : Area.Key == MoreKey ? "The other areas of the app" : Area.Description;
    public override bool ShowFilterBar => false;

    public IReadOnlyList<AreaTile> Main { get; }
    public IReadOnlyList<AreaTile> More { get; }
    public AreaTile MoreTile { get; }
    public IEnumerable<AreaTile> AllAreas => Main.Concat(More);
    /// <summary>Level 1: the main tiles + MORE.</summary>
    public IReadOnlyList<AreaTile> Level1 => Main.Append(MoreTile).ToList();

    /// <summary>Area whose list is shown (null = the tiles; MORE = the secondary tiles).</summary>
    [ObservableProperty] private AreaTile? _area;
    public bool ShowTiles => Area is null;
    public bool ShowMore => Area?.Key == MoreKey;
    public bool ShowList => Area is { Key: not MoreKey };
    public string AreaTitle => Area?.Title ?? "";
    public string AreaDescription => Area?.Description ?? "";

    partial void OnAreaChanged(AreaTile? value)
    {
        foreach (var n in new[] { nameof(ShowTiles), nameof(ShowMore), nameof(ShowList), nameof(AreaTitle), nameof(AreaDescription), nameof(Title), nameof(Subtitle) }) OnPropertyChanged(n);
        AreaChanged?.Invoke(value);
    }

    /// <summary>Raised when an item is opened (the shell navigates and shows HOME &gt; AREA &gt; ITEM).</summary>
    public event Action<AreaTile, AreaLink>? LinkOpened;
    public event Action<AreaTile?>? AreaChanged;

    /// <summary>The first area that lists a page (for the breadcrumb when a page is opened from the sidebar).</summary>
    public (AreaTile Area, AreaLink Link)? Find(string pageKey, string? tab = null)
    {
        foreach (var a in AllAreas)
            foreach (var l in a.Links)
                if (l.PageKey == pageKey && (tab is null || l.Tab is null || l.Tab == tab)) return (a, l);
        return null;
    }

    [RelayCommand] private void OpenTile(AreaTile? tile) { if (tile != null) Area = tile; }
    [RelayCommand] private void OpenLink(AreaLink? link) { if (link != null && Area != null) LinkOpened?.Invoke(Area, link); }
    [RelayCommand] public void Back() => Area = Area is { IsMain: false, Key: not MoreKey } ? MoreTile : null;

    protected override void NavigateTo(NavTarget target)
    {
        Area = target.Key is null ? null : target.Key == MoreKey ? MoreTile : AllAreas.FirstOrDefault(a => a.Key == target.Key);
    }

    public ObservableCollection<HomeFigure> Figures { get; } = new();
    public ObservableCollection<HomeLine> Attention { get; } = new();
    public ObservableCollection<HomeLine> Recent { get; } = new();

    [RelayCommand] private void OpenFigure(HomeFigure? f) { if (f != null) Ctx.Nav.Go("Tracking", new NavTarget("Tracking", Key: f.Tab)); }
    [RelayCommand] private void OpenLine(HomeLine? l) { if (l != null) Ctx.Nav.Go(l.PageKey, l.Tab is null ? null : new NavTarget(l.PageKey, Key: l.Tab)); }
    [RelayCommand] private void NewStatement() => Ctx.Nav.Go("Tracking", new NavTarget("Tracking", Key: "ENTRY"));
    [RelayCommand] private void OpenPlans() => Ctx.Nav.Go("Tracking", new NavTarget("Tracking", Key: "PLANS"));
    [RelayCommand] private void AskRaffaello() => Ctx.Nav.OpenAsk();
    [RelayCommand] private void ImportRecon() => Ctx.Nav.Go("Ledger");

    private void FillPanels()
    {
        var p = Project;
        var s = p.Snapshot;
        Figures.Clear();
        var buildings = BuildingFilter is { } bf ? new[] { bf } : Buildings.All;
        var rates = p.Workflow.Rates();
        foreach (var b in buildings)
        {
            var claims = s.Claims.Where(c => string.Equals(c.Building, b, StringComparison.OrdinalIgnoreCase)).ToList();
            var caps = s.RoomQtys.Where(q => string.Equals(q.Building, b, StringComparison.OrdinalIgnoreCase)).ToList();
            if (claims.Count == 0 && caps.Count == 0) continue;
            // same definitions as the recon check: claimed = every compared, non-rework claim; remaining = total - claimed (over-claims reduce it)
            var all = LedgerRules.Balances(caps, claims).Values.ToList();
            var bal = all.Where(x => x.HasCap).ToList();
            var total = bal.Sum(x => x.ProjectQty);
            var claimed = all.Sum(x => x.Claimed);
            var remaining = total - claimed;
            double value = 0, priced = 0;
            foreach (var x in bal.Where(x => x.Remaining > 0))
                if (rates.Resolve("", b, 0, x.Stage, x.Item).Rate is double r) { value += x.Remaining * r; priced += x.Remaining; }
            var over = bal.Where(x => x.IsOver).ToList();
            var pending = claims.Count(LengthCheck.IsPending);
            var emt = claims.Where(c => c.Rework || c.Stage.Equals("EMT", StringComparison.OrdinalIgnoreCase)).Sum(c => c.Qty);
            Figures.Add(new(b, "Total", total.ToString("#,0"), "points (PROJECT QTY)", "PROJECT QTY"));
            Figures.Add(new(b, "Claimed", claimed.ToString("#,0"), "points, all subcontractors (recon definition)", "LEDGER"));
            Figures.Add(new(b, "Remaining", remaining.ToString("#,0"), priced > 0 ? $"SAR {value:#,0} ({priced / Math.Max(1, remaining):P0} priced)" : "SAR value: import the rate mapping", "SUMMARY"));
            Figures.Add(new(b, "Over", over.Count.ToString("#,0"), $"lines, {over.Sum(x => x.Claimed - x.ProjectQty):#,0.#} points over", "CONTROL", over.Count > 0 ? "OVER" : ""));
            Figures.Add(new(b, "15 m checks", pending.ToString("#,0"), "lines waiting for route-length proof", "CONTROL", pending > 0 ? "DUE" : ""));
            Figures.Add(new(b, "EMT / rework", emt.ToString("#,0.#"), "points (not against the totals)", "LEDGER"));
            Figures.Add(new(b, "% complete", total <= 0 ? "-" : (claimed / total).ToString("P1"), "claimed / total", "DASHBOARD"));
        }
        Attention.Clear();
        var allClaims = s.Claims.Where(c => InBuilding(c.Building)).ToList();
        var allBal = LedgerRules.Balances(s.RoomQtys.Where(q => InBuilding(q.Building)), allClaims).Values.Where(x => x.HasCap && x.IsOver).ToList();
        foreach (var g in allBal.SelectMany(x => x.BySubcontractor.Keys.Select(sub => (sub, x))).GroupBy(t => t.sub).OrderByDescending(g => g.Count()).Take(3))
            Attention.Add(new($"Over-claims: {g.Key}", g.Count().ToString(), "location | stage | item keys over the total where this subcontractor claimed", "Tracking", "CONTROL", "OVER"));
        var refs = Buildings.All.Where(b => BuildingFilter is null || b == BuildingFilter)
            .SelectMany(b => InvoiceStatusList.Build(b, s.Claims.Where(c => c.Building == b), InvoiceStatusList.LoadReference(TrackingViewModel.ReferenceFolder, b), s.MappingRules)).ToList();
        var differs = refs.Count(r => r.Status == DrawingStatus.Differs);
        var wip = refs.Count(r => r.Status == DrawingStatus.Pending);
        if (differs > 0) Attention.Add(new("Invoices differ from the drawings", differs.ToString(), "drawn qty vs invoiced beyond 2 points / 2 %", "Tracking", "INVOICES", "CHECK"));
        if (wip > 0) Attention.Add(new("Invoices work in progress", wip.ToString(), "invoice known, drawing / claim lines pending", "Tracking", "INVOICES"));
        var drafts = 0;
        try { drafts = StatementDraft.List(TrackingViewModel.DraftFolder).Count(f => StatementDraft.Load(f) is { Posted: false }); } catch (Exception) { }
        if (drafts > 0) Attention.Add(new("Statements in draft", drafts.ToString(), "saved statement entries not posted yet", "Tracking", "ENTRY", "DUE"));
        var pend = allClaims.Count(c => LengthCheck.IsPending(c) || HeightCheck.IsPending(c));
        if (pend > 0) Attention.Add(new("Height / 15 m checks", pend.ToString(), "claim lines held until decided", "Checks", null, "DUE"));
        var wirOpen = s.Wirs.Count(w => w.Kind == "WIR" && w.Status == WirStatus.Open);
        var wirLate = s.Wirs.Count(w => w.Kind == "WIR" && w.Status == WirStatus.Open && (p.Options.Today - w.SubmittedAt.Date).TotalDays > p.Options.WirDueDays);
        if (wirOpen > 0) Attention.Add(new("WIRs open", wirOpen.ToString(), $"{wirLate} overdue", "Wir", null, wirLate > 0 ? "DUE" : ""));
        var mirs = s.Wirs.Count(w => w.Kind == "MIR" && w.Status == WirStatus.Open);
        if (mirs > 0) Attention.Add(new("MIRs waiting", mirs.ToString(), "material inspection requests open", "Wir", null));
        var matFlags = p.Queue.Count(q => q.Category == "MATERIAL");
        if (matFlags > 0) Attention.Add(new("Material flags", matFlags.ToString(), "PO / DN / invoice checks", "Materials", null, "CHECK"));
        if (Attention.Count == 0) Attention.Add(new("Nothing waiting", "", "all clear for this building", "Dashboard", null, "OK"));

        Recent.Clear();
        var items = new List<(DateTime At, HomeLine Line)>();
        foreach (var i in s.Imports.OrderByDescending(i => i.ImportedAt).Take(4))
            items.Add((i.ImportedAt, new HomeLine($"Import {i.Kind}", i.ImportedAt.ToString("dd MMM HH:mm"), $"{i.FileName}  |  {i.Rows:N0} rows", "Ledger", null)));
        foreach (var st in s.Statements.OrderByDescending(x => x.At).Take(4))
            items.Add((st.At, new HomeLine($"Statement {st.StatementNo} {st.Subcontractor}", st.At.ToString("dd MMM HH:mm"), $"{st.Direction}  |  {st.Lines} lines", "Tracking", "LEDGER")));
        foreach (var c in allClaims.Where(c => c.Source is "MANUAL" or "STATEMENT").OrderByDescending(c => c.EnteredAt).Take(3))
            items.Add((c.EnteredAt, new HomeLine($"Posted {c.Subcontractor} INV {c.InvoiceNo}", c.EnteredAt.ToString("dd MMM HH:mm"), $"{c.Room} {c.Stage} {c.Item} {c.Qty:0.##}", "Tracking", "LEDGER")));
        foreach (var x in items.OrderByDescending(x => x.At).Take(6)) Recent.Add(x.Line);
        if (Recent.Count == 0) Recent.Add(new("No activity yet", "", "imports, statements and postings show here", "Ledger", null));
    }

    protected override void Refresh()
    {
        try { FillPanels(); } catch (Exception) { /* the panels are a summary - never block HOME */ }
        var p = Project;
        var s = p.Snapshot;
        string N(int n, string what) => n == 0 ? $"no {what}" : $"{n:N0} {what}";
        var claims = s.Claims.Where(c => InBuilding(c.Building)).ToList();
        var caps = s.RoomQtys.Where(q => InBuilding(q.Building)).ToList();
        var bal = LedgerRules.Balances(caps, claims);
        var project = bal.Values.Where(b => b.HasCap).Sum(b => b.ProjectQty);
        var claimed = bal.Values.Sum(b => b.Claimed);
        var remaining = project - claimed;
        var over = bal.Values.Count(b => b.HasCap && b.IsOver);
        var pendingChecks = claims.Count(c => HeightCheck.IsPending(c) || LengthCheck.IsPending(c));
        var subInv = s.SubInvoices.Count(x => x.Status is SubInvoiceStatus.Rejected or SubInvoiceStatus.Submitted or SubInvoiceStatus.Draft);
        var statements = s.Invoices.Count(x => x.Status is InvoiceStatus.Received or InvoiceStatus.Redo);
        var mirs = s.Wirs.Count(w => w.Kind == "MIR" && w.Status == WirStatus.Open);
        var wirsOpen = s.Wirs.Count(w => w.Kind == "WIR" && w.Status == WirStatus.Open);
        var queued = s.AconexDocs.Count(d => d.Queued);
        var mat = p.Queue.Count(q => q.Category == "MATERIAL");

        Set("SC", $"{N(subInv, "invoices pending")} · {N(pendingChecks, "checks")}");
        Set("MAT", $"{N(mirs, "MIRs waiting")} · {N(mat, "flags")}");
        Set("ACONEX", $"{N(wirsOpen, "WIRs open")} · {N(queued, "queued")}");
        Set("OWNER", $"{N(s.BoqItems.Count, "BOQ items")} · {N(s.Statements.Count, "statements")}");
        Set("REPORTS", s.Imports.Count == 0 ? "no imports yet" : $"last import {s.Imports.Max(i => i.ImportedAt):dd MMM} · {s.Imports.Count:N0} imports");
        Set("TRACKING", project <= 0 ? "no PROJECT QTY imported yet" : $"{remaining:N0} pts left · {over:N0} over");
        Set("SUMMARY", project <= 0 ? $"{claims.Count:N0} claim lines" : $"{claimed / project:P0} complete · {claims.Count:N0} lines");
        Set("CONTRACTS", $"{N(s.Contracts.Count, "contracts")}  |  {N(s.ContractItems.Count, "rate items")}");
        Set("SITE", $"{N(statements, "statements to check")}  |  {N(wirsOpen, "WIRs open")}");
        Set("CABLES", N(p.Queue.Count(q => q.Category == "CABLES"), "cable flags"));
        Set("INSIGHTS", N(p.Queue.Count(q => q.Category == "INSIGHT" && q.Severity >= Verdict.Check), "anomalies to look at"));
        Set("VARIATIONS", "register and submissions");
        Set("DRAWINGS", $"{N(s.RoomShapes.Count, "room shapes")}");
        Set("ASSISTANT", "ask about any line, invoice or WIR");
        Set("SETTINGS", "data file, updates, theme");
        MoreTile.Status = $"{More.Count} more areas";

        foreach (var a in AllAreas)
            foreach (var l in a.Links)
                l.Count = (l.PageKey, l.Tab) switch
                {
                    ("Tracking", "CONTROL") => over > 0 ? $"{over:N0} over" : "",
                    ("Tracking", "ROOMS") => $"{caps.Select(q => q.Room).Distinct().Count():N0} locations",
                    ("Tracking", "LEDGER") or ("Tracking", "ENTRY") => $"{claims.Count:N0} lines",
                    ("Checks", _) => pendingChecks > 0 ? $"{pendingChecks:N0} pending" : "",
                    ("Invoices", _) => subInv > 0 ? $"{subInv} pending" : "",
                    ("Statements", _) => statements > 0 ? $"{statements} to check" : "",
                    ("Wir", _) => wirsOpen + mirs > 0 ? $"{wirsOpen + mirs} open" : "",
                    ("Contracts", _) => $"{s.Contracts.Count} contracts",
                    _ => "",
                };
    }

    private void Set(string key, string status)
    {
        var a = AllAreas.FirstOrDefault(x => x.Key == key);
        if (a != null) a.Status = status;
    }
}