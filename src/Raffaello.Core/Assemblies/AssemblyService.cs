using System.Globalization;
using System.Text.Json;
using Raffaello.Core.Boq;
using Raffaello.Core.Coding;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Materials;

namespace Raffaello.Core.Assemblies;

public static class SourceKinds
{
    /// <summary>Subcontract item (ContractItems).</summary>
    public const string Contract = "CONTRACT";
    /// <summary>E-Promise budget list / owner BOQ code (BoqItems).</summary>
    public const string Boq = "BOQ";
    /// <summary>Imported owner BOQ workbook line (Materials module BoqLines).</summary>
    public const string BoqLine = "BOQLINE";
    /// <summary>Free text (variation new item, typed description).</summary>
    public const string Text = "TEXT";
    public static readonly string[] All = { Contract, Boq, BoqLine, Text };
}

/// <summary>An item that can be broken down: a contract item, an owner BOQ item / line or a typed description.</summary>
public sealed class AssemblySource
{
    public string Kind { get; init; } = SourceKinds.Text;
    public long Id { get; init; }
    /// <summary>Stable key: CONTRACT "contractNo|itemNo", BOQ / BOQLINE the BOQ code (or file|row), TEXT the normalised description.</summary>
    public string Key { get; init; } = "";
    /// <summary>Item no. / BOQ code shown to the user.</summary>
    public string Code { get; init; } = "";
    public string Description { get; init; } = "";
    public string Unit { get; init; } = "";
    public double Qty { get; init; }
    /// <summary>Contract / BOQ rate (the reference the built-up rate is compared with).</summary>
    public double Rate { get; init; }
    public string Group { get; init; } = "";
    public string ContractNo { get; init; } = "";
    public string Building { get; init; } = "";

    public ItemSourceKind ParseKind => Kind == SourceKinds.Contract ? ItemSourceKind.Contract : Kind == SourceKinds.Text ? ItemSourceKind.Text : ItemSourceKind.Boq;
    public string RateLabel => Kind == SourceKinds.Contract ? "CONTRACT" : Kind == SourceKinds.Text ? "ENTERED" : "BOQ";
    public string Title => (Code.Length > 0 ? Code + "  " : "") + (Description.Length > 90 ? Description[..90] + "..." : Description);
    public string KindLabel => Kind switch { SourceKinds.Contract => $"CONTRACT {ContractNo}", SourceKinds.Boq => "E-PROMISE BOQ", SourceKinds.BoqLine => "OWNER BOQ", _ => "TEXT" };

    public static AssemblySource FromText(string description, string unit = "", double qty = 1, double rate = 0) => new()
    {
        Kind = SourceKinds.Text, Key = Fingerprints.NormalizeDescription(description), Description = description, Unit = unit, Qty = qty, Rate = rate,
    };
}

/// <summary>
/// [assemblies] Entry point for the screens and the CLI: item sources (contract items, E-Promise BOQ, owner BOQ lines), parsed and
/// user-corrected specs, the library, prices (PO lines via the Materials module, price lists, manual), subcontract labour rates, single
/// breakdowns, bulk requirements and the Claude assist.
/// </summary>
public sealed class AssemblyService
{
    private readonly Func<ProjectSnapshot> _project;
    private readonly Func<MaterialsSnapshot?> _materials;

    public AssemblyService(IAssemblyStore store, Func<ProjectSnapshot> project, Func<MaterialsSnapshot?> materials)
    {
        Store = store; _project = project; _materials = materials;
    }

    public IAssemblyStore Store { get; }
    public AssemblyLibrary Library { get; private set; } = new();
    public AssemblySettings Settings { get; private set; } = new();
    public List<AsmPrice> PriceRows { get; private set; } = new();
    public List<AsmItemSpec> SpecRows { get; private set; } = new();
    public PriceBook Prices { get; private set; } = new(Array.Empty<AsmPrice>(), Array.Empty<PoPrice>(), new AssemblySettings());
    public ContractLabour Labour { get; private set; } = new(Array.Empty<ContractItem>());
    public ProjectSnapshot Project => _project();
    public MaterialsSnapshot? Materials => _materials();

    /// <summary>Average route length per point for an item type (hook for the Drawings module). Null = template default.</summary>
    public Func<string, double?>? RouteLength { get; set; }
    /// <summary>Route length per point for a spec with its source (Drawings takeoff averages per item type / room type / system, see
    /// <see cref="Wiring.DrawingRouteLengths"/>). Null or no hit = template default.</summary>
    public Func<ItemSpec, RouteLengthHit?>? RouteLengthFor { get; set; }

    public void Reload()
    {
        Store.EnsureSchema();
        Library = Store.LoadLibrary();
        Settings = Store.LoadSettings();
        PriceRows = Store.Prices();
        SpecRows = Store.ItemSpecs();
        MaterialsSnapshot? mats = null;
        try { mats = _materials(); } catch (Exception) { /* materials not available (other data source) */ }
        var stick = Library.Globals.FirstOrDefault(g => g.Name == "stick_len")?.Value ?? 3;
        Prices = new PriceBook(PriceRows, mats is null ? Array.Empty<PoPrice>() : PriceBook.FromMaterials(mats), Settings, stick);
        Labour = new ContractLabour(Project.ContractItems);
    }

    // ------------------------------------------------------------------ sources

    public List<AssemblySource> Sources(string? kind = null, string? building = null)
    {
        var res = new List<AssemblySource>();
        var p = Project;
        var contracts = p.Contracts.ToDictionary(c => c.ContractNo, StringComparer.OrdinalIgnoreCase);
        if (kind is null or SourceKinds.Contract)
            foreach (var i in p.ContractItems.OrderBy(i => i.ContractNo).ThenBy(i => i.Order))
            {
                var b = contracts.TryGetValue(i.ContractNo, out var c) ? c.Building : "";
                if (building != null && b.Length > 0 && !b.Equals(building, StringComparison.OrdinalIgnoreCase)) continue;
                res.Add(new AssemblySource
                {
                    Kind = SourceKinds.Contract, Id = i.Id, Key = $"{i.ContractNo}|{i.ItemNo}", Code = i.ItemNo, Description = i.Description, Unit = i.Unit, Qty = i.Qty, Rate = i.Rate,
                    Group = i.Section, ContractNo = i.ContractNo, Building = b,
                });
            }
        if (kind is null or SourceKinds.Boq)
            foreach (var b in p.BoqItems.Where(b => b.Description.Length > 0).OrderBy(b => b.ItemCode))
                res.Add(new AssemblySource { Kind = SourceKinds.Boq, Id = b.Id, Key = b.ItemCode, Code = b.ItemCode, Description = b.Description, Unit = b.Unit, Qty = b.BoqQty, Rate = b.Rate, Group = b.Bill });
        if (kind is null or SourceKinds.BoqLine)
        {
            MaterialsSnapshot? m = null;
            try { m = _materials(); } catch (Exception) { }
            if (m != null)
                foreach (var l in m.BoqLines.Where(l => !l.IsHeading).OrderBy(l => l.Source).ThenBy(l => l.Sheet).ThenBy(l => l.RowNo))
                    res.Add(new AssemblySource
                    {
                        Kind = SourceKinds.BoqLine, Id = l.Id, Key = l.BoqCode.Length > 0 ? l.BoqCode : $"{l.Source}|{l.Sheet}|{l.RowNo}", Code = l.BoqCode.Length > 0 ? l.BoqCode : l.ItemNo,
                        Description = l.Description, Unit = l.Unit, Qty = l.Qty, Rate = l.Rate, Group = l.System,
                    });
        }
        return res;
    }

    public AssemblySource? FindSource(string kind, long id) => Sources(kind).FirstOrDefault(s => s.Id == id);

    // ------------------------------------------------------------------ specs

    public AsmItemSpec? StoredSpec(AssemblySource src) =>
        SpecRows.FirstOrDefault(s => s.SourceKind == src.Kind && s.SourceKey.Equals(src.Key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The user-corrected spec when there is one, else the parsed one.</summary>
    public ItemSpec SpecFor(AssemblySource src)
    {
        var stored = StoredSpec(src);
        var spec = stored is not null ? ItemSpec.FromJson(stored.SpecJson) : null;
        return spec ?? ItemParser.Parse(src.Description, src.Unit, src.ParseKind);
    }

    public Dictionary<string, double> OverridesFor(AssemblySource src)
    {
        var stored = StoredSpec(src);
        if (stored is null || string.IsNullOrWhiteSpace(stored.ParamsJson)) return new(StringComparer.OrdinalIgnoreCase);
        try { return new Dictionary<string, double>(JsonSerializer.Deserialize<Dictionary<string, double>>(stored.ParamsJson) ?? new(), StringComparer.OrdinalIgnoreCase); }
        catch (JsonException) { return new(StringComparer.OrdinalIgnoreCase); }
    }

    public AsmItemSpec SaveSpec(AssemblySource src, ItemSpec spec, IReadOnlyDictionary<string, double>? overrides, string templateCode, bool confirmed)
    {
        var row = StoredSpec(src) ?? new AsmItemSpec { SourceKind = src.Kind, SourceKey = src.Key };
        row.Description = src.Description;
        row.SpecJson = spec.ToJson();
        row.ParamsJson = overrides is { Count: > 0 } ? JsonSerializer.Serialize(overrides) : "";
        row.TemplateCode = templateCode;
        row.Confirmed = confirmed;
        var saved = Store.SaveItemSpec(row);
        SpecRows = Store.ItemSpecs();
        return saved;
    }

    public AssemblyTemplate? TemplateFor(AssemblySource src, ItemSpec spec, string? templateCode = null) =>
        Library.ByCode(templateCode) ?? Library.ByCode(StoredSpec(src)?.TemplateCode) ?? Library.ForType(spec.ItemType);

    // ------------------------------------------------------------------ breakdowns

    public Breakdown Run(AssemblySource src, ItemSpec? spec = null, IReadOnlyDictionary<string, double>? overrides = null, string? templateCode = null)
    {
        spec ??= SpecFor(src);
        var template = TemplateFor(src, spec, templateCode);
        if (template is null)
            return new Breakdown { Spec = spec, Unit = spec.Unit, Flags = { spec.Recognised ? $"no template for {spec.ItemType} - add one in the template editor" : "item type not recognised - pick the type" }, ReferenceRate = src.Rate > 0 ? src.Rate : null, ReferenceLabel = src.RateLabel };
        var ctx = new CalcContext
        {
            Globals = Library.Globals, Prices = Prices, LabourRates = Labour, Settings = Settings, Source = src.ParseKind,
            Overrides = overrides ?? OverridesFor(src), RouteLength = RouteLength, RouteLengthFor = RouteLengthFor, ReferenceRate = src.Rate > 0 ? src.Rate : null, ReferenceLabel = src.RateLabel,
        };
        return AssemblyCalculator.Calculate(spec, template, ctx);
    }

    public Breakdown RunText(string description, string unit = "", double referenceRate = 0) => Run(AssemblySource.FromText(description, unit, 1, referenceRate));

    /// <summary>Breakdown of many items x their quantities -> material requirements vs PO / DN / installed.</summary>
    public BulkResult Bulk(IEnumerable<AssemblySource> items, bool useInstalled = true)
    {
        var installed = useInstalled ? BulkRequirements.InstalledFromInvoices(Project) : new Dictionary<string, double>();
        var stick = Library.Globals.FirstOrDefault(g => g.Name == "stick_len")?.Value ?? 3;
        MaterialsSnapshot? mats = null;
        try { mats = _materials(); } catch (Exception) { }
        return BulkRequirements.Run(items.Select(s => (s, Run(s), installed.TryGetValue(s.Key, out var q) ? q : 0)), mats, Settings.PoMatchScore, stick);
    }

    /// <summary>Parse coverage of the sources (share with a recognised item type).</summary>
    public (int Total, int Recognised, Dictionary<string, int> ByType) Coverage(IEnumerable<AssemblySource> sources) =>
        ItemParser.Coverage(sources.Select(SpecFor));

    // ------------------------------------------------------------------ prices

    public PriceListImport ReadPriceList(string path, string supplier, DateTime? date) => PriceListImporter.Read(path, supplier, date);

    public int CommitPriceList(PriceListImport import)
    {
        var n = Store.ImportPrices(import.Prices, Path.GetFileName(import.FileName));
        Reload();
        return n;
    }

    public AsmPrice SetManualPrice(string spec, string unit, double price, string note = "")
    {
        var key = PriceBook.KeyOf(spec);
        var row = PriceRows.FirstOrDefault(p => p.Source == PriceSources.Manual && p.Key == key && Units.Normalize(p.Unit) == Units.Normalize(unit))
                  ?? new AsmPrice { Source = PriceSources.Manual, Description = spec, Unit = unit };
        row.Price = price;
        row.PriceDate = DateTime.Today;
        row.SourceRef = Store.User;
        row.Notes = note;
        var saved = Store.SavePrice(row);
        Reload();
        return saved;
    }

    /// <summary>Saves a new template from an assist proposal or a copy (code made unique).</summary>
    public AssemblyTemplate SaveAsNewTemplate(AssemblyTemplate t)
    {
        var code = t.Header.Code.Trim().ToUpperInvariant();
        if (code.Length == 0) code = "USER";
        var baseCode = code; var n = 1;
        while (Library.ByCode(code) != null) code = $"{baseCode}-{++n}";
        t.Header.Code = code;
        t.Header.Id = 0;
        foreach (var p in t.Params) p.Id = 0;
        foreach (var c in t.Components) c.Id = 0;
        Store.SaveTemplate(t);
        Reload();
        return Library.ByCode(code) ?? t;
    }

    public static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
