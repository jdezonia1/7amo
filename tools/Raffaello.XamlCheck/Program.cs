using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

// Raffaello XAML binding check (Linux, no WPF runtime): walks every screen with the type its DataContext will have and reports
// {Binding} paths that do not exist on that type - WPF drops those silently (an overlay stuck open, an empty column, a dead button).
// Usage: dotnet run --project tools/Raffaello.XamlCheck -- <app bin folder> <src/Raffaello.App>   (exit 1 when problems are found)

var bin = Path.GetFullPath(args.Length > 0 ? args[0] : "src/Raffaello.App/bin/Debug/net8.0-windows10.0.19041.0");
var src = Path.GetFullPath(args.Length > 1 ? args[1] : "src/Raffaello.App");
var checker = new Checker(bin, src);
return checker.Run();

sealed class Checker
{
    private const string Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private const string X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private readonly string _src;
    private readonly MetadataLoadContext _mlc;
    private readonly List<Assembly> _asms = new();
    private readonly Dictionary<string, string> _xamlByClass = new();          // x:Class -> file
    private readonly Dictionary<string, XDocument> _docs = new();
    private readonly HashSet<string> _done = new();
    private readonly Queue<(string File, Type? Ctx, string Why)> _work = new();
    private readonly List<string> _problems = new();
    private readonly HashSet<string> _reached = new();
    private int _checked;

    public Checker(string bin, string src)
    {
        _src = src;
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var nuget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages", "microsoft.windowsdesktop.app.ref");
        // Linux/cloud: NuGet cache; Windows: the SDK's packs folder (dotnet\packs\Microsoft.WindowsDesktop.App.Ref\<ver>)
        if (!Directory.Exists(nuget)) nuget = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(runtime)))!, "packs", "Microsoft.WindowsDesktop.App.Ref");
        var wpfRef = Directory.GetDirectories(nuget).Where(d => Directory.Exists(Path.Combine(d, "ref", "net8.0"))).OrderBy(d => d).Last();
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // WPF reference assemblies first: the runtime folder only has a WindowsBase facade
        foreach (var f in Directory.GetFiles(Path.Combine(wpfRef, "ref", "net8.0"), "*.dll")) files.TryAdd(Path.GetFileName(f), f);
        foreach (var f in Directory.GetFiles(runtime, "*.dll")) files.TryAdd(Path.GetFileName(f), f);
        foreach (var f in Directory.GetFiles(bin, "*.dll")) files.TryAdd(Path.GetFileName(f), f);
        _mlc = new MetadataLoadContext(new PathAssemblyResolver(files.Values), "System.Private.CoreLib");
        foreach (var name in new[] { "Raffaello.dll", "Raffaello.Core.dll", "Raffaello.Automation.dll", "Raffaello.Ocr.dll" })
            if (File.Exists(Path.Combine(bin, name))) _asms.Add(_mlc.LoadFromAssemblyPath(Path.Combine(bin, name)));
    }

    public int Run()
    {
        foreach (var f in Directory.GetFiles(_src, "*.xaml", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var doc = XDocument.Load(f, LoadOptions.SetLineInfo);
            _docs[f] = doc;
            if (doc.Root?.Attribute(XName.Get("Class", X))?.Value is { } cls) _xamlByClass[cls] = f;
        }
        // windows: DataContext = <ctor parameter> / new T(...) in the code-behind
        foreach (var (cls, file) in _xamlByClass)
        {
            var cs = file + ".cs";
            if (!File.Exists(cs)) continue;
            var code = File.ReadAllText(cs);
            Type? t = null;
            var m = Regex.Match(code, @"DataContext\s*=\s*(?:\w+\s*=\s*)?new\s+([\w.]+)\s*\(");
            if (m.Success) t = FindType(m.Groups[1].Value);
            else if (Regex.Match(code, @"\bDataContext\s*=\s*(\w+)\s*;") is { Success: true } a
                     && Regex.Match(code, @"([\w.<>]+)\s+" + a.Groups[1].Value + @"\s*[,)]") is { Success: true } p)
                t = FindType(p.Groups[1].Value);
            if (t != null) _work.Enqueue((file, t, "code-behind"));
        }
        while (_work.Count > 0)
        {
            var (file, ctx, why) = _work.Dequeue();
            if (!_done.Add(file + "|" + ctx?.FullName)) continue;
            _reached.Add(file);
            var root = _docs[file].Root!;
            Walk(root, file, ctx, isRoot: true);
        }
        // views never reached: the type the code-behind casts DataContext to
        foreach (var (cls, file) in _xamlByClass.Where(kv => !_reached.Contains(kv.Value)))
        {
            var cs = file + ".cs";
            if (File.Exists(cs) && Regex.Match(File.ReadAllText(cs), @"DataContext\s+(?:is|as)\s+([\w.]+)") is { Success: true } m && FindType(m.Groups[1].Value) is { } t)
            {
                _work.Enqueue((file, t, "code-behind cast"));
                while (_work.Count > 0)
                {
                    var (f2, c2, _) = _work.Dequeue();
                    if (!_done.Add(f2 + "|" + c2?.FullName)) continue;
                    _reached.Add(f2);
                    Walk(_docs[f2].Root!, f2, c2, isRoot: true);
                }
            }
        }
        var unreached = _xamlByClass.Values.Where(f => !_reached.Contains(f) && _docs[f].Root!.Name.LocalName != "ResourceDictionary" && _docs[f].Root!.Name.LocalName != "Application").Select(Rel).OrderBy(x => x).ToList();
        Console.WriteLine($"XAML binding check: {_reached.Count} screens, {_checked} bindings checked, {_problems.Count} problems");
        if (unreached.Count > 0) Console.WriteLine("  not checked (DataContext type unknown): " + string.Join(", ", unreached));
        foreach (var p in _problems.Distinct().OrderBy(p => p)) Console.WriteLine("  " + p);
        return _problems.Count > 0 ? 1 : 0;
    }

    private string Rel(string f) => Path.GetRelativePath(_src, f);

    private void Walk(XElement e, string file, Type? ctx, bool isRoot = false, Type? itemType = null)
    {
        var name = e.Name.LocalName;
        // property elements: <ListBox.ItemTemplate> etc.
        if (name.Contains('.'))
        {
            var prop = name[(name.IndexOf('.') + 1)..];
            if (prop is "Resources") { foreach (var c in e.Elements()) WalkResource(c, file); return; }
            if (prop is "Template") return;                                  // ControlTemplate: TemplatedParent context
            if (prop is "Style" or "ItemContainerStyle" or "CellStyle" or "RowStyle" or "ElementStyle" or "EditingElementStyle")
            {
                var styleCtx = prop is "Style" ? ctx : itemType;
                foreach (var c in e.Elements()) WalkStyle(c, file, styleCtx);
                return;
            }
            var templ = prop is "ItemTemplate" or "CellTemplate" or "CellEditingTemplate" or "Columns" or "ContentTemplate" or "HeaderTemplate" or "SelectedItemTemplate" or "RowDetailsTemplate";
            if (prop is "ContentTemplate" or "HeaderTemplate") { foreach (var c in e.Elements()) Walk(c, file, null); return; }
            foreach (var c in e.Elements()) Walk(c, file, templ ? itemType : ctx, itemType: itemType);
            return;
        }
        if (name is "ControlTemplate") return;
        if (name is "DataTemplate" or "HierarchicalDataTemplate")
        {
            var dt = e.Attribute("DataType")?.Value is { } d ? TypeFromMarkup(d, e) : ctx;
            if (name == "HierarchicalDataTemplate") CheckAttr(e, "ItemsSource", file, dt);
            foreach (var c in e.Elements()) Walk(c, file, dt);
            return;
        }
        if (name is "Style") { WalkStyle(e, file, ctx); return; }

        // DataContext is evaluated against the parent context, everything else on the element against the new one
        var newCtx = ctx;
        if (e.Attribute("DataContext") is { } dcAttr && ParseBinding(dcAttr.Value) is { } dcb)
        {
            newCtx = dcb.Skip ? null : Resolve(ctx, dcb.Path, file, e, "DataContext");
            _checked++;
        }
        Type? newItems = null;
        foreach (var a in e.Attributes())
        {
            if (a.IsNamespaceDeclaration || a.Name.LocalName == "DataContext" || a.Name.NamespaceName.Length > 0 && a.Name.NamespaceName != Wpf) continue;
            if (ParseBinding(a.Value) is not { } b) continue;
            _checked++;
            if (b.Skip) continue;
            // DataGrid column Binding="{Binding X}" is relative to the row item
            var against = a.Name.LocalName is "Binding" or "SelectedValueBinding" or "TextBinding" && name.EndsWith("Column") ? newCtx : newCtx;
            var t = Resolve(against, b.Path, file, e, a.Name.LocalName);
            if (a.Name.LocalName == "ItemsSource") newItems = ElementType(t);
        }
        // <Binding Path=...> children of MultiBinding / property elements
        foreach (var bEl in e.Elements().Where(c => c.Name.LocalName.Contains('.')).SelectMany(c => c.Elements()).Where(c => c.Name.LocalName is "Binding" or "MultiBinding"))
            CheckBindingElement(bEl, file, newCtx);

        // a local control (another screen) inherits this context
        if (!isRoot && ResolveTag(e) is { } tag && tag.FullName is { } full && _xamlByClass.TryGetValue(full, out var childFile) && e.Attribute("DataContext") is null)
            _work.Enqueue((childFile, newCtx, Rel(file)));
        else if (!isRoot && ResolveTag(e) is { } tag2 && tag2.FullName is { } full2 && _xamlByClass.TryGetValue(full2, out var childFile2))
            _work.Enqueue((childFile2, newCtx, Rel(file)));

        var colsItem = newItems ?? (name.EndsWith("Column") ? itemType : null);
        foreach (var c in e.Elements())
        {
            if (c.Name.LocalName is "Binding" or "MultiBinding") continue;
            if (name.EndsWith("Column")) { Walk(c, file, itemType, itemType: itemType); continue; }
            Walk(c, file, newCtx, itemType: newItems ?? (c.Name.LocalName.Contains('.') ? colsItem : null));
        }
        // DataGrid columns: <DataGrid.Columns><DataGridTextColumn Binding="{Binding X}"/> are relative to the row item
    }

    private void WalkResource(XElement e, string file)
    {
        if (e.Name.LocalName is "DataTemplate" or "HierarchicalDataTemplate" && e.Attribute("DataType") is not null) Walk(e, file, null);
    }

    private void WalkStyle(XElement style, string file, Type? ctx)
    {
        if (ctx is null) return;
        foreach (var s in style.Descendants().Where(d => d.Name.LocalName is "Setter" or "DataTrigger"))
        {
            if (s.Ancestors().Any(a => a.Name.LocalName is "ControlTemplate" or "Setter.Value" or "DataTemplate")) continue;
            if (s.Name.LocalName == "DataTrigger") CheckAttr(s, "Binding", file, ctx);
            else if (s.Attribute("Value") is { } v && ParseBinding(v.Value) is { Skip: false } b) { _checked++; Resolve(ctx, b.Path, file, s, "Setter " + s.Attribute("Property")?.Value); }
        }
    }

    private void CheckAttr(XElement e, string attr, string file, Type? ctx)
    {
        if (e.Attribute(attr) is { } a && ParseBinding(a.Value) is { } b) { _checked++; if (!b.Skip) Resolve(ctx, b.Path, file, e, attr); }
    }

    private void CheckBindingElement(XElement b, string file, Type? ctx)
    {
        if (b.Name.LocalName == "MultiBinding") { foreach (var c in b.Elements().Where(c => c.Name.LocalName == "Binding")) CheckBindingElement(c, file, ctx); return; }
        if (b.Attribute("ElementName") != null || b.Attribute("RelativeSource") != null || b.Attribute("Source") != null) return;
        var path = b.Attribute("Path")?.Value ?? "";
        _checked++;
        if (path.Length > 0) Resolve(ctx, path, file, b, "Binding");
    }

    private sealed record ParsedBinding(string Path, bool Skip);

    /// <summary>"{Binding A.B, Converter=...}" -> path A.B; ElementName / RelativeSource / Source / x:Reference -> skip.</summary>
    private static ParsedBinding? ParseBinding(string v)
    {
        v = v.Trim();
        if (!v.StartsWith("{Binding") || v.StartsWith("{Binding}") && v.Length == 9) return v == "{Binding}" ? new("", true) : null;
        if (!(v.Length == 8 + 1 || v[8] is ' ' or '}' or ',')) return null;   // {BindingX}
        var inner = v[8..^1];
        var parts = SplitTop(inner);
        var path = "";
        var skip = false;
        foreach (var p in parts.Select(p => p.Trim()).Where(p => p.Length > 0))
        {
            var eq = p.IndexOf('=');
            if (eq < 0) { path = p; continue; }
            var key = p[..eq].Trim();
            if (key == "Path") path = p[(eq + 1)..].Trim();
            if (key is "ElementName" or "RelativeSource" or "Source") skip = true;
        }
        return new(path, skip || path.Length == 0 || path == ".");
    }

    private static List<string> SplitTop(string s)
    {
        // Commas inside {..} or '..' (e.g. StringFormat='#,##0.00;-#,##0.00;-') do not split arguments.
        var res = new List<string>(); var depth = 0; var start = 0; var quoted = false;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == (char)39) quoted = !quoted;   // single quote
            else if (quoted) continue;
            else if (s[i] == '{') depth++;
            else if (s[i] == '}') depth--;
            else if (s[i] == ',' && depth == 0) { res.Add(s[start..i]); start = i + 1; }
        }
        res.Add(s[start..]);
        return res;
    }

    private Type? Resolve(Type? ctx, string path, string file, XElement e, string what)
    {
        if (ctx is null || path.Length == 0) return null;
        var t = ctx;
        foreach (var raw in path.Split('.'))
        {
            var seg = raw.Trim();
            if (seg.Length == 0 || seg.StartsWith('(') || seg.Contains(')')) return null;   // attached property
            var idx = seg.IndexOf('[');
            var hasIndexer = idx >= 0;
            if (idx == 0) return null;
            if (hasIndexer) seg = seg[..idx];
            if (Opaque(t)) return null;
            var p = FindProperty(t, seg);
            if (p is null && (t.IsAbstract || t.IsInterface))
            {
                // a list of a base type showing subclasses: fine when a concrete subclass has the property
                p = _asms.SelectMany(SafeTypes).Where(x => !x.IsAbstract && Assignable(t, x)).Select(x => FindProperty(x, seg)).FirstOrDefault(x => x != null);
                if (p != null) return null;
            }
            if (p is null)
            {
                var line = ((IXmlLineInfo)e).LineNumber;
                _problems.Add($"{Rel(file)}:{line}  {what}=\"{{Binding {path}}}\" - '{seg}' is not a property of {Short(t)}");
                return null;
            }
            t = p.PropertyType;
            if (hasIndexer) return null;
        }
        return t;
    }

    private static bool Opaque(Type t) =>
        t.FullName is "System.Object" or "System.Data.DataRowView" or "System.Data.DataView" or "System.Dynamic.ExpandoObject"
        || t.IsGenericParameter || t.FullName?.StartsWith("System.Windows.Data.CollectionView") == true || t.Name is "ICollectionView" or "ListCollectionView"
        || (t.IsInterface && t.GetInterfaces().All(i => i.Name != "INotifyPropertyChanged") && t.Name.StartsWith("IEnumerable"));

    private static PropertyInfo? FindProperty(Type t, string name)
    {
        for (var cur = t; cur != null; cur = cur.BaseType)
        {
            var p = cur.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).FirstOrDefault(x => x.Name == name);
            if (p != null) return p;
        }
        if (t.IsInterface) foreach (var i in t.GetInterfaces()) if (i.GetProperty(name) is { } ip) return ip;
        return null;
    }

    private static bool Assignable(Type b, Type x)
    {
        try { return b.IsAssignableFrom(x); } catch (Exception) { return false; }
    }

    private static IEnumerable<Type> SafeTypes(Assembly a)
    {
        try { return a.GetTypes(); } catch (ReflectionTypeLoadException ex) { return ex.Types.Where(x => x != null)!; }
    }

    private static Type? ElementType(Type? t)
    {
        if (t is null) return null;
        if (t.IsArray) return t.GetElementType();
        var ien = (t.IsInterface && t.IsGenericType && t.GetGenericTypeDefinition().FullName == "System.Collections.Generic.IEnumerable`1" ? t : null)
                  ?? t.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition().FullName == "System.Collections.Generic.IEnumerable`1");
        if (ien is null) return null;
        var el = ien.GetGenericArguments()[0];
        return el.FullName?.StartsWith("System.Collections.Generic.KeyValuePair") == true ? el : el;
    }

    private static string Short(Type t) => t.IsGenericType ? t.Name[..t.Name.IndexOf('`')] + "<" + string.Join(",", t.GetGenericArguments().Select(Short)) + ">" : t.Name;

    private Type? FindType(string name)
    {
        name = name.Trim();
        foreach (var a in _asms)
        {
            var t = a.GetType(name) ?? a.GetTypes().FirstOrDefault(x => x.Name == name || x.FullName == name || x.FullName?.EndsWith("." + name) == true);
            if (t != null) return t;
        }
        return null;
    }

    /// <summary>"{x:Type vm:Foo}" -> type via the element's xmlns declarations.</summary>
    private Type? TypeFromMarkup(string v, XElement scope)
    {
        var m = Regex.Match(v, @"\{x:Type\s+(?:TypeName=)?([\w]+):([\w.]+)\s*\}");
        if (!m.Success) m = Regex.Match(v, @"^([\w]+):([\w.]+)$");
        if (!m.Success) return null;
        var ns = scope.GetNamespaceOfPrefix(m.Groups[1].Value)?.NamespaceName ?? "";
        return TypeFromClrNs(ns, m.Groups[2].Value);
    }

    private Type? ResolveTag(XElement e)
    {
        var ns = e.Name.NamespaceName;
        if (!ns.StartsWith("clr-namespace:")) return null;
        return TypeFromClrNs(ns, e.Name.LocalName);
    }

    private Type? TypeFromClrNs(string ns, string name)
    {
        if (!ns.StartsWith("clr-namespace:")) return null;
        var clr = ns["clr-namespace:".Length..].Split(';')[0];
        foreach (var a in _asms) if (a.GetType(clr + "." + name) is { } t) return t;
        return null;
    }
}
