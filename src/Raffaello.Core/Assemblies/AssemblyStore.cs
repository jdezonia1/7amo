using System.Globalization;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Remote;

namespace Raffaello.Core.Assemblies;

/// <summary>[assemblies] Entity types of the assembly module (tables Asm*).</summary>
public static class AssemblyEntities
{
    public static readonly Type[] All = { typeof(AsmTemplate), typeof(AsmParam), typeof(AsmComponent), typeof(AsmPrice), typeof(AsmItemSpec), typeof(AsmSetting) };

    private static bool _registered;

    /// <summary>Makes the remote store, the offline cache and the local -> server migration know the Asm* tables.</summary>
    public static void Register()
    {
        if (_registered) return;
        foreach (var t in All) EntityMeta.Register(t);
        _registered = true;
    }
}

/// <summary>
/// [assemblies] Persistence of the assembly library (templates, parameters, components), prices, edited item specs and settings.
/// SQLite (<see cref="SqliteAssemblyStore"/>, same data file) or the Raffaello server (<see cref="RemoteAssemblyStore"/>).
/// </summary>
public interface IAssemblyStore
{
    string User { get; }
    void EnsureSchema();
    /// <summary>The library; seeds the default templates the first time.</summary>
    AssemblyLibrary LoadLibrary();
    List<AsmPrice> Prices();
    List<AsmItemSpec> ItemSpecs();
    AssemblySettings LoadSettings();
    void SaveSettings(AssemblySettings s);
    /// <summary>Saves the template header and replaces its parameters and components.</summary>
    void SaveTemplate(AssemblyTemplate t);
    void DeleteTemplate(AssemblyTemplate t);
    void SaveGlobals(IReadOnlyList<AsmParam> globals);
    AsmPrice SavePrice(AsmPrice p);
    void DeletePrice(AsmPrice p);
    /// <summary>Replaces the price-list rows of the same source file with the new ones.</summary>
    int ImportPrices(IReadOnlyList<AsmPrice> prices, string sourceRef);
    AsmItemSpec SaveItemSpec(AsmItemSpec s);
    /// <summary>Adds the seeded templates that are missing (by code); with <paramref name="resetSeeded"/> seeded templates are restored to the defaults.</summary>
    int SeedDefaults(bool resetSeeded = false);
}

/// <summary>Shared rules of the SQLite and server stores (they differ only in the primitives).</summary>
public abstract class AssemblyStoreBase : IAssemblyStore
{
    public abstract string User { get; }
    public abstract void EnsureSchema();
    protected abstract List<T> Rows<T>() where T : Entity, new();
    protected abstract void Write(Action<IStoreBatch> work, string summary);

    public AssemblyLibrary LoadLibrary()
    {
        var headers = Rows<AsmTemplate>();
        if (headers.Count == 0)
        {
            SeedDefaults();
            headers = Rows<AsmTemplate>();
        }
        var pars = Rows<AsmParam>();
        var comps = Rows<AsmComponent>();
        var lib = new AssemblyLibrary { Globals = pars.Where(p => p.TemplateId == 0).OrderBy(p => p.Name).ToList() };
        if (lib.Globals.Count == 0) lib.Globals.AddRange(DefaultLibrary.GlobalParams());
        foreach (var h in headers.OrderBy(h => h.ItemType).ThenBy(h => h.Code))
            lib.Templates.Add(new AssemblyTemplate
            {
                Header = h,
                Params = pars.Where(p => p.TemplateId == h.Id).OrderBy(p => p.Id).ToList(),
                Components = comps.Where(c => c.TemplateId == h.Id).OrderBy(c => c.Order).ThenBy(c => c.Id).ToList(),
            });
        return lib;
    }

    public List<AsmPrice> Prices() => Rows<AsmPrice>();
    public List<AsmItemSpec> ItemSpecs() => Rows<AsmItemSpec>();

    public AssemblySettings LoadSettings()
    {
        var rows = Rows<AsmSetting>().GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);
        var s = new AssemblySettings();
        double D(string k, double def) => rows.TryGetValue(k, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : def;
        s.OverheadPct = D(nameof(s.OverheadPct), s.OverheadPct);
        s.ProfitPct = D(nameof(s.ProfitPct), s.ProfitPct);
        s.PoMatchScore = D(nameof(s.PoMatchScore), s.PoMatchScore);
        s.PriceListMatchScore = D(nameof(s.PriceListMatchScore), s.PriceListMatchScore);
        if (rows.TryGetValue(nameof(s.LabourMode), out var lm) && AssemblySettings.LabourModes.Contains(lm)) s.LabourMode = lm;
        if (rows.TryGetValue(nameof(s.Currency), out var cur) && cur.Length > 0) s.Currency = cur;
        return s;
    }

    public void SaveSettings(AssemblySettings s)
    {
        var want = new Dictionary<string, string>
        {
            [nameof(s.OverheadPct)] = s.OverheadPct.ToString(CultureInfo.InvariantCulture),
            [nameof(s.ProfitPct)] = s.ProfitPct.ToString(CultureInfo.InvariantCulture),
            [nameof(s.PoMatchScore)] = s.PoMatchScore.ToString(CultureInfo.InvariantCulture),
            [nameof(s.PriceListMatchScore)] = s.PriceListMatchScore.ToString(CultureInfo.InvariantCulture),
            [nameof(s.LabourMode)] = s.LabourMode,
            [nameof(s.Currency)] = s.Currency,
        };
        var rows = Rows<AsmSetting>();
        Write(w =>
        {
            foreach (var (k, v) in want)
            {
                var r = rows.FirstOrDefault(x => x.Name.Equals(k, StringComparison.OrdinalIgnoreCase));
                if (r is null) w.Insert(new AsmSetting { Name = k, Value = v });
                else if (r.Value != v) { r.Value = v; w.Update(r); }
            }
        }, $"Assembly settings: OH {s.OverheadPct:P1}, profit {s.ProfitPct:P1}, labour {s.LabourMode}");
    }

    public void SaveTemplate(AssemblyTemplate t)
    {
        if (string.IsNullOrWhiteSpace(t.Header.Code)) throw new InvalidOperationException("The template needs a code.");
        var dup = Rows<AsmTemplate>().FirstOrDefault(h => h.Code.Equals(t.Header.Code.Trim(), StringComparison.OrdinalIgnoreCase) && h.Id != t.Header.Id);
        if (dup != null) throw new InvalidOperationException($"Template code {t.Header.Code} already exists.");
        var problems = TemplateCheck.Problems(t, LoadLibrary().Globals);
        if (problems.Count > 0) throw new InvalidOperationException("Fix the formulas first: " + string.Join("; ", problems.Take(5)));
        var oldPars = t.Header.Id > 0 ? Rows<AsmParam>().Where(p => p.TemplateId == t.Header.Id).ToList() : new List<AsmParam>();
        var oldComps = t.Header.Id > 0 ? Rows<AsmComponent>().Where(c => c.TemplateId == t.Header.Id).ToList() : new List<AsmComponent>();
        t.Header.Code = t.Header.Code.Trim().ToUpperInvariant();
        Write(w =>
        {
            if (t.Header.Id > 0) w.Update(t.Header); else w.Insert(t.Header);
            foreach (var p in oldPars) w.Delete(p);
            foreach (var c in oldComps) w.Delete(c);
            var order = 0;
            foreach (var p in t.Params) w.Insert(new AsmParam { TemplateId = t.Header.Id, Name = p.Name.Trim(), Value = p.Value, Unit = p.Unit, Label = p.Label, Note = p.Note, Confirmed = p.Confirmed });
            foreach (var c in t.Components)
                w.Insert(new AsmComponent
                {
                    TemplateId = t.Header.Id, Order = ++order, Key = c.Key.Trim(), Name = c.Name, Spec = c.Spec, Unit = c.Unit, QtyFormula = c.QtyFormula, WastePct = c.WastePct,
                    Stage = c.Stage, Kind = c.Kind, LabourBasis = c.LabourBasis, LabourCategory = c.LabourCategory, DefaultPrice = c.DefaultPrice, Notes = c.Notes,
                });
        }, $"Assembly template {t.Header.Code} saved: {t.Params.Count} parameters, {t.Components.Count} components");
    }

    public void DeleteTemplate(AssemblyTemplate t)
    {
        if (t.Header.Id <= 0) return;
        var pars = Rows<AsmParam>().Where(p => p.TemplateId == t.Header.Id).ToList();
        var comps = Rows<AsmComponent>().Where(c => c.TemplateId == t.Header.Id).ToList();
        Write(w =>
        {
            foreach (var p in pars) w.Delete(p);
            foreach (var c in comps) w.Delete(c);
            w.Delete(t.Header);
        }, $"Assembly template {t.Header.Code} deleted");
    }

    public void SaveGlobals(IReadOnlyList<AsmParam> globals)
    {
        var old = Rows<AsmParam>().Where(p => p.TemplateId == 0).ToList();
        Write(w =>
        {
            foreach (var p in old) w.Delete(p);
            foreach (var p in globals) w.Insert(new AsmParam { TemplateId = 0, Name = p.Name.Trim(), Value = p.Value, Unit = p.Unit, Label = p.Label, Note = p.Note, Confirmed = p.Confirmed });
        }, $"Assembly global parameters saved ({globals.Count})");
    }

    public AsmPrice SavePrice(AsmPrice p)
    {
        p.Key = PriceBook.KeyOf(p.Description);
        Write(w => { if (p.Id > 0) w.Update(p); else w.Insert(p); }, $"Price {p.Description}: {p.Price:N2} {p.Currency}/{p.Unit} ({p.Source})");
        return p;
    }

    public void DeletePrice(AsmPrice p) => Write(w => w.Delete(p), $"Price {p.Description} deleted");

    public int ImportPrices(IReadOnlyList<AsmPrice> prices, string sourceRef)
    {
        var old = Rows<AsmPrice>().Where(p => p.Source == PriceSources.PriceList && p.SourceRef.Equals(sourceRef, StringComparison.OrdinalIgnoreCase)).ToList();
        Write(w =>
        {
            foreach (var p in old) w.Delete(p);
            foreach (var p in prices) { p.SourceRef = sourceRef; p.Source = PriceSources.PriceList; if (p.Key.Length == 0) p.Key = PriceBook.KeyOf(p.Description); w.Insert(p); }
        }, $"Price list {sourceRef}: {prices.Count} prices (replaced {old.Count})");
        return prices.Count;
    }

    public AsmItemSpec SaveItemSpec(AsmItemSpec s)
    {
        var existing = s.Id > 0 ? null : Rows<AsmItemSpec>().FirstOrDefault(x => x.SourceKind == s.SourceKind && x.SourceKey == s.SourceKey);
        if (existing != null) { s.Id = existing.Id; s.RowVersion = existing.RowVersion; }
        Write(w => { if (s.Id > 0) w.Update(s); else w.Insert(s); }, $"Item spec {s.SourceKind} {s.SourceKey} saved");
        return s;
    }

    public int SeedDefaults(bool resetSeeded = false)
    {
        var seed = DefaultLibrary.Build();
        var existing = Rows<AsmTemplate>();
        var globals = Rows<AsmParam>().Where(p => p.TemplateId == 0).ToList();
        var n = 0;
        foreach (var t in seed.Templates)
        {
            var old = existing.FirstOrDefault(h => h.Code.Equals(t.Code, StringComparison.OrdinalIgnoreCase));
            if (old != null && !(resetSeeded && old.Origin == "SEED")) continue;
            if (old != null) { t.Header.Id = old.Id; t.Header.RowVersion = old.RowVersion; }
            SaveTemplateRaw(t);
            n++;
        }
        var missing = seed.Globals.Where(g => !globals.Any(x => x.Name.Equals(g.Name, StringComparison.OrdinalIgnoreCase))).ToList();
        if (missing.Count > 0) Write(w => { foreach (var g in missing) w.Insert(g); }, $"Assembly global parameters seeded ({missing.Count})");
        return n;
    }

    /// <summary>Seeding path without the formula check (the defaults are tested).</summary>
    private void SaveTemplateRaw(AssemblyTemplate t)
    {
        var oldPars = t.Header.Id > 0 ? Rows<AsmParam>().Where(p => p.TemplateId == t.Header.Id).ToList() : new List<AsmParam>();
        var oldComps = t.Header.Id > 0 ? Rows<AsmComponent>().Where(c => c.TemplateId == t.Header.Id).ToList() : new List<AsmComponent>();
        Write(w =>
        {
            if (t.Header.Id > 0) w.Update(t.Header); else w.Insert(t.Header);
            foreach (var p in oldPars) w.Delete(p);
            foreach (var c in oldComps) w.Delete(c);
            foreach (var p in t.Params) { p.TemplateId = t.Header.Id; w.Insert(p); }
            foreach (var c in t.Components) { c.TemplateId = t.Header.Id; w.Insert(c); }
        }, $"Assembly template {t.Code} seeded (defaults to be confirmed)");
    }
}

/// <summary>SQLite implementation (same data file as the project, own Asm* tables).</summary>
public sealed class SqliteAssemblyStore : AssemblyStoreBase
{
    private readonly Inner _db;

    private sealed class Inner : SideStore
    {
        public Inner(string path, string user) : base(path, user) { }
        protected override IEnumerable<Type> Tables => AssemblyEntities.All;
        protected override IEnumerable<string> Indexes => new[]
        {
            "CREATE INDEX IF NOT EXISTS IX_AsmParams_T ON AsmParams(TemplateId);",
            "CREATE INDEX IF NOT EXISTS IX_AsmComponents_T ON AsmComponents(TemplateId);",
        };
    }

    private sealed class BatchAdapter : IStoreBatch
    {
        private readonly SideStore.SideBatch _b;
        public BatchAdapter(SideStore.SideBatch b) => _b = b;
        public T Insert<T>(T entity) where T : Entity => _b.Insert(entity);
        public int InsertMany<T>(IEnumerable<T> entities) where T : Entity { var n = 0; foreach (var e in entities) { _b.Insert(e); n++; } return n; }
        public void Update<T>(T entity) where T : Entity, new() => _b.Update(entity);
        public void Delete<T>(T entity) where T : Entity => _b.Delete(entity);
    }

    public SqliteAssemblyStore(string path, string user) => _db = new Inner(path, user);

    public override string User => _db.User;
    public override void EnsureSchema() => _db.EnsureSchema();
    protected override List<T> Rows<T>() => _db.All<T>();
    protected override void Write(Action<IStoreBatch> work, string summary) => _db.Batch(b => work(new BatchAdapter(b)), summary);
}

/// <summary>Server implementation (tables served by AssembliesServerModule; offline queue, conflicts and audit from <see cref="RemoteProjectStore"/>).</summary>
public sealed class RemoteAssemblyStore : AssemblyStoreBase
{
    private readonly RemoteProjectStore _r;
    static RemoteAssemblyStore() => AssemblyEntities.Register();
    public RemoteAssemblyStore(RemoteProjectStore r) => _r = r;

    public override string User => _r.User;
    public override void EnsureSchema() { }   // the server creates the tables
    protected override List<T> Rows<T>() => _r.All<T>();
    protected override void Write(Action<IStoreBatch> work, string summary) => _r.Batch(work, summary);
}

/// <summary>Picks the SQLite or the server store for the current data source on every call (the user can switch in Settings).</summary>
public sealed class AssemblyStoreSelector : IAssemblyStore
{
    private readonly Func<IProjectStore> _store;
    private readonly Func<string> _path;
    private readonly Func<string> _user;
    private object? _for;
    private string _forPath = "";
    private IAssemblyStore? _impl;

    public AssemblyStoreSelector(Func<IProjectStore> store, Func<string> dataPath, Func<string> user) { _store = store; _path = dataPath; _user = user; }

    private IAssemblyStore Impl
    {
        get
        {
            var s = _store();
            var path = s is RemoteProjectStore ? "" : _path();
            if (!ReferenceEquals(s, _for) || _impl is null || path != _forPath)
            {
                _impl = s switch
                {
                    RemoteProjectStore r => new RemoteAssemblyStore(r),
                    _ => new SqliteAssemblyStore(path, _user()),
                };
                _impl.EnsureSchema();
                _for = s; _forPath = path;
            }
            return _impl;
        }
    }

    public string User => Impl.User;
    public void EnsureSchema() => Impl.EnsureSchema();
    public AssemblyLibrary LoadLibrary() => Impl.LoadLibrary();
    public List<AsmPrice> Prices() => Impl.Prices();
    public List<AsmItemSpec> ItemSpecs() => Impl.ItemSpecs();
    public AssemblySettings LoadSettings() => Impl.LoadSettings();
    public void SaveSettings(AssemblySettings s) => Impl.SaveSettings(s);
    public void SaveTemplate(AssemblyTemplate t) => Impl.SaveTemplate(t);
    public void DeleteTemplate(AssemblyTemplate t) => Impl.DeleteTemplate(t);
    public void SaveGlobals(IReadOnlyList<AsmParam> globals) => Impl.SaveGlobals(globals);
    public AsmPrice SavePrice(AsmPrice p) => Impl.SavePrice(p);
    public void DeletePrice(AsmPrice p) => Impl.DeletePrice(p);
    public int ImportPrices(IReadOnlyList<AsmPrice> prices, string sourceRef) => Impl.ImportPrices(prices, sourceRef);
    public AsmItemSpec SaveItemSpec(AsmItemSpec s) => Impl.SaveItemSpec(s);
    public int SeedDefaults(bool resetSeeded = false) => Impl.SeedDefaults(resetSeeded);
}

/// <summary>Template checks for the editor: unknown names / syntax errors in the formulas, duplicate keys.</summary>
public static class TemplateCheck
{
    /// <summary>Names every template can use besides its own parameters and earlier component keys.</summary>
    public static readonly string[] BuiltIn =
    {
        "conduit_size", "wire_size", "wire_cores", "cable_size", "cable_cores", "gangs", "ways", "amps", "tray_width", "compartments", "watts",
        "route_len", "drop_len", "bends", "with_term", "with_cpc", "is_pvc", "is_emt", "is_rs", "is_flex", "is_ceiling", "is_wall", "mount_stand",
        "is_high", "is_concealed", "is_fr", "is_earth", "is_armoured", "facade", "cpc_size",
    };

    public static List<string> Problems(AssemblyTemplate t, IEnumerable<AsmParam> globals)
    {
        var res = new List<string>();
        var known = new List<string>(BuiltIn);
        known.AddRange(globals.Select(g => g.Name));
        known.AddRange(t.Params.Select(p => p.Name));
        foreach (var dupe in t.Params.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)) res.Add($"parameter {dupe.Key} twice");
        foreach (var dupe in t.Components.Where(c => c.Key.Length > 0).GroupBy(c => c.Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)) res.Add($"component key {dupe.Key} twice");
        foreach (var c in t.Components.OrderBy(c => c.Order))
        {
            var q = Formula.Validate(c.QtyFormula, known);
            if (q.Length > 0) res.Add($"{c.Name} qty: {q}");
            if (c.DefaultPrice.Trim().Length > 0)
            {
                var p = Formula.Validate(c.DefaultPrice, known);
                if (p.Length > 0) res.Add($"{c.Name} price: {p}");
            }
            if (c.Key.Length > 0) known.Add(c.Key);
        }
        return res;
    }
}
