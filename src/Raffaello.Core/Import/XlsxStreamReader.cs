using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Raffaello.Core.Import;

/// <summary>One row of a worksheet read in streaming mode: cached values (and formulas) by 1-based column.</summary>
public sealed class XlsxRow
{
    public int Number { get; init; }
    public Dictionary<int, string> Values { get; } = new();
    public Dictionary<int, string> Formulas { get; } = new();

    public string Get(int col) => Values.TryGetValue(col, out var v) ? v : "";
    public string Get(string col) => Get(XlsxStreamReader.ColumnIndex(col));
    public string? Formula(string col) => Formulas.TryGetValue(XlsxStreamReader.ColumnIndex(col), out var f) ? f : null;

    public double? Number_(int col)
    {
        var s = Get(col).Trim();
        if (s.Length == 0) return null;
        return double.TryParse(s.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }
    public double? Num(string col) => Number_(XlsxStreamReader.ColumnIndex(col));
    public bool IsEmpty => Values.Values.All(string.IsNullOrWhiteSpace);
}

/// <summary>
/// Minimal forward-only .xlsx/.xlsm reader (zip + XmlReader). Reads cached cell values without loading the workbook,
/// so a 10 MB tracker with a 37 MB formula sheet is read in seconds. Also exposes drawing parts and media for the plans.
/// </summary>
public sealed class XlsxStreamReader : IDisposable
{
    private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PkgRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private readonly ZipArchive _zip;
    private readonly FileStream? _fs;
    private List<string>? _shared;
    private Dictionary<string, string>? _sheets; // name -> part path

    public XlsxStreamReader(string path)
    {
        _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        _zip = new ZipArchive(_fs, ZipArchiveMode.Read);
    }

    public XlsxStreamReader(Stream stream) => _zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

    public void Dispose()
    {
        _zip.Dispose();
        _fs?.Dispose();
    }

    public static int ColumnIndex(string col)
    {
        var n = 0;
        foreach (var ch in col.ToUpperInvariant())
        {
            if (ch < 'A' || ch > 'Z') break;
            n = n * 26 + (ch - 'A' + 1);
        }
        return n;
    }

    public static string ColumnLetter(int index)
    {
        var sb = new StringBuilder();
        while (index > 0) { var m = (index - 1) % 26; sb.Insert(0, (char)('A' + m)); index = (index - 1) / 26; }
        return sb.ToString();
    }

    public IReadOnlyDictionary<string, string> Sheets => _sheets ??= LoadSheets();

    public bool HasSheet(string name) => Sheets.Keys.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));

    public string? FindSheet(Func<string, bool> predicate) => Sheets.Keys.FirstOrDefault(predicate);

    private Dictionary<string, string> LoadSheets()
    {
        var wb = XDocument.Load(Open("xl/workbook.xml"));
        var rels = Rels("xl/_rels/workbook.xml.rels");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in wb.Descendants(XName.Get("sheet", MainNs)))
        {
            var id = (string?)s.Attribute(XName.Get("id", RelNs));
            if (id != null && rels.TryGetValue(id, out var target)) result[(string)s.Attribute("name")!] = Resolve("xl/", target);
        }
        return result;
    }

    private Dictionary<string, string> Rels(string path)
    {
        var d = new Dictionary<string, string>();
        var e = _zip.GetEntry(path);
        if (e is null) return d;
        using var st = e.Open();
        foreach (var r in XDocument.Load(st).Descendants(XName.Get("Relationship", PkgRelNs)))
            d[(string)r.Attribute("Id")!] = (string)r.Attribute("Target")!;
        return d;
    }

    private static string Resolve(string baseDir, string target)
    {
        if (target.StartsWith('/')) return target.TrimStart('/');
        var parts = (baseDir + target).Split('/').ToList();
        for (var i = 0; i < parts.Count; i++)
        {
            if (parts[i] == "..") { parts.RemoveAt(i); if (i > 0) { parts.RemoveAt(i - 1); i--; } i--; }
            else if (parts[i] == ".") { parts.RemoveAt(i); i--; }
        }
        return string.Join('/', parts);
    }

    private Stream Open(string path) => (_zip.GetEntry(path) ?? throw new InvalidDataException($"Missing part {path}")).Open();

    private List<string> Shared()
    {
        if (_shared != null) return _shared;
        _shared = new List<string>();
        var e = _zip.GetEntry("xl/sharedStrings.xml");
        if (e is null) return _shared;
        using var st = e.Open();
        using var xr = XmlReader.Create(st, new XmlReaderSettings { IgnoreWhitespace = false });
        StringBuilder? cur = null;
        xr.Read();
        while (!xr.EOF)
        {
            if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "si")
            {
                if (xr.IsEmptyElement) { _shared.Add(""); xr.Read(); continue; }
                cur = new StringBuilder();
                xr.Read();
                continue;
            }
            if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "rPh") { xr.Skip(); continue; }
            if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "t" && cur != null)
            {
                if (xr.IsEmptyElement) { xr.Read(); continue; }
                cur.Append(xr.ReadElementContentAsString());
                continue;
            }
            if (xr.NodeType == XmlNodeType.EndElement && xr.LocalName == "si" && cur != null) { _shared.Add(cur.ToString()); cur = null; }
            xr.Read();
        }
        return _shared;
    }

    /// <summary>Streams the rows of a sheet (only rows present in the XML; empty rows are skipped).</summary>
    public IEnumerable<XlsxRow> ReadRows(string sheetName, int maxRow = int.MaxValue)
    {
        var part = Sheets.FirstOrDefault(k => string.Equals(k.Key, sheetName, StringComparison.OrdinalIgnoreCase)).Value
                   ?? throw new InvalidDataException($"Sheet '{sheetName}' not found.");
        var shared = Shared();
        using var st = Open(part);
        using var xr = XmlReader.Create(st, new XmlReaderSettings { IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Ignore });
        XlsxRow? row = null;
        var rowCounter = 0;
        while (xr.Read())
        {
            if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "row")
            {
                var r = xr.GetAttribute("r");
                rowCounter = r != null ? int.Parse(r, CultureInfo.InvariantCulture) : rowCounter + 1;
                if (rowCounter > maxRow) yield break;
                row = new XlsxRow { Number = rowCounter };
                if (xr.IsEmptyElement) { yield return row; row = null; }
            }
            else if (xr.NodeType == XmlNodeType.EndElement && xr.LocalName == "row" && row != null)
            {
                yield return row;
                row = null;
            }
            else if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "c" && row != null)
            {
                var refAttr = xr.GetAttribute("r") ?? "";
                var col = ColumnIndex(refAttr);
                if (col == 0) col = row.Values.Count == 0 ? 1 : row.Values.Keys.Max() + 1;
                var type = xr.GetAttribute("t");
                if (xr.IsEmptyElement) continue;
                string? value = null, formula = null;
                using (var sub = xr.ReadSubtree())
                {
                    sub.Read();   // <c>
                    sub.Read();
                    while (!sub.EOF)
                    {
                        if (sub.NodeType == XmlNodeType.Element && sub.LocalName is "v" or "f" or "t" && !sub.IsEmptyElement)
                        {
                            var name = sub.LocalName;
                            var text = sub.ReadElementContentAsString();
                            if (name == "v") value = text;
                            else if (name == "f") formula = text;
                            else value = (value ?? "") + text;
                        }
                        else sub.Read();
                    }
                }
                if (value != null && type == "s" && int.TryParse(value, out var si)) value = si < shared.Count ? shared[si] : "";
                if (value != null && type == "b") value = value == "1" ? "TRUE" : "FALSE";
                if (value != null) row.Values[col] = value;
                if (formula != null) row.Formulas[col] = formula;
            }
        }
    }

    /// <summary>Path of the drawing part attached to a sheet, if any.</summary>
    public string? DrawingPartOf(string sheetName)
    {
        var part = Sheets.FirstOrDefault(k => string.Equals(k.Key, sheetName, StringComparison.OrdinalIgnoreCase)).Value;
        if (part is null) return null;
        var dir = part[..(part.LastIndexOf('/') + 1)];
        var rels = Rels(dir + "_rels/" + part[(part.LastIndexOf('/') + 1)..] + ".rels");
        var target = rels.Values.FirstOrDefault(t => t.Contains("drawings/", StringComparison.OrdinalIgnoreCase));
        return target is null ? null : Resolve(dir, target);
    }

    public XDocument LoadXml(string part)
    {
        using var st = Open(part);
        return XDocument.Load(st);
    }

    /// <summary>Relationship id -> part path for a part (e.g. images of a drawing).</summary>
    public Dictionary<string, string> RelationshipsOf(string part)
    {
        var dir = part[..(part.LastIndexOf('/') + 1)];
        return Rels(dir + "_rels/" + part[(part.LastIndexOf('/') + 1)..] + ".rels").ToDictionary(k => k.Key, k => Resolve(dir, k.Value));
    }

    public byte[] ReadBytes(string part)
    {
        using var st = Open(part);
        using var ms = new MemoryStream();
        st.CopyTo(ms);
        return ms.ToArray();
    }
}
