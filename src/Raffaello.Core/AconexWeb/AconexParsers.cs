using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Raffaello.Core.AconexWeb;

/// <summary>Finds columns by header text, never by position: case, spacing, punctuation and sort arrows are ignored.</summary>
public sealed class ColumnMap
{
    private readonly List<string> _headers;
    private readonly Dictionary<string, int> _byNorm = new(StringComparer.Ordinal);

    public ColumnMap(IEnumerable<string> headers)
    {
        _headers = headers.ToList();
        for (var i = 0; i < _headers.Count; i++)
        {
            var n = Normalize(_headers[i]);
            if (n.Length > 0 && !_byNorm.ContainsKey(n)) _byNorm[n] = i;
        }
    }

    public IReadOnlyList<string> Headers => _headers;

    public static string Normalize(string? h)
    {
        var sb = new StringBuilder();
        foreach (var ch in (h ?? "").ToLowerInvariant())
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }

    /// <summary>Index of the first header matching one of the names: exact (normalised) first, then "starts with".</summary>
    public int Find(IEnumerable<string> names)
    {
        var list = names.Select(Normalize).Where(n => n.Length > 0).ToList();
        foreach (var n in list) if (_byNorm.TryGetValue(n, out var i)) return i;
        foreach (var n in list)
            for (var i = 0; i < _headers.Count; i++)
            {
                var h = Normalize(_headers[i]);
                if (h.Length > 0 && h.StartsWith(n, StringComparison.Ordinal) && h.Length - n.Length <= 6) return i;
            }
        return -1;
    }

    public static string Cell(RawRow row, int index) => index >= 0 && index < row.Cells.Count ? Clean(row.Cells[index]) : "";

    public static string Clean(string? s) => Regex.Replace(WebUtility.HtmlDecode(s ?? ""), @"[ \t ]+", " ").Trim();
}

/// <summary>Dates as Aconex shows them (configurable formats, day first).</summary>
public static class AconexDates
{
    public static DateTime? Parse(string? text, IEnumerable<string> formats)
    {
        var s = ColumnMap.Clean(text);
        if (s.Length == 0) return null;
        // "18/05/2026 10:22 AST" -> drop the zone
        s = Regex.Replace(s, @"\s+[A-Z]{2,4}$", "");
        if (DateTime.TryParseExact(s, formats.ToArray(), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d)) return d;
        // first date-looking token
        var m = Regex.Match(s, @"\d{1,2}[/\-.]\d{1,2}[/\-.]\d{4}|\d{4}-\d{2}-\d{2}|\d{1,2}-[A-Za-z]{3}-\d{2,4}");
        if (m.Success && DateTime.TryParseExact(m.Value, formats.Concat(new[] { "d/M/yyyy", "d-M-yyyy", "d.M.yyyy", "yyyy-MM-dd", "d-MMM-yyyy", "d-MMM-yy" }).ToArray(),
                CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return d;
        return null;
    }
}

/// <summary>
/// Minimal, tolerant HTML table reader (no external parser): handles thead/tbody or plain rows, th/td, colspan group rows,
/// nested tags and entities. Used on saved pages and in tests; the browser reads the live DOM the same way (cell innerText).
/// </summary>
public static class HtmlTableParser
{
    private static readonly Regex TableRx = new(@"<table\b(?<attrs>[^>]*)>(?<body>.*?)</table>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex RowRx = new(@"<tr\b(?<attrs>[^>]*)>(?<body>.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex CellRx = new(@"<(?<tag>t[hd])\b(?<attrs>[^>]*)>(?<body>.*?)</\k<tag>>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex HrefRx = new(@"<a\b[^>]*href\s*=\s*[""'](?<h>[^""']*)[""']", RegexOptions.IgnoreCase);

    /// <summary>All tables in the document whose opening tag contains <paramref name="attrContains"/> (or every table).</summary>
    public static List<RawTable> ParseAll(string html, string? attrContains = null)
    {
        var list = new List<RawTable>();
        foreach (Match t in TableRx.Matches(html))
        {
            if (attrContains != null && !t.Groups["attrs"].Value.Contains(attrContains, StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(ParseTable(t.Groups["body"].Value));
        }
        return list;
    }

    public static RawTable? ParseFirst(string html, string? attrContains = null) => ParseAll(html, attrContains).FirstOrDefault();

    private static RawTable ParseTable(string body)
    {
        var t = new RawTable();
        var rowIndex = 0;
        foreach (Match r in RowRx.Matches(body))
        {
            var cells = CellRx.Matches(r.Groups["body"].Value).Cast<Match>().ToList();
            if (cells.Count == 0) continue;
            var isHeader = t.Headers.Count == 0 && cells.All(c => c.Groups["tag"].Value.Equals("th", StringComparison.OrdinalIgnoreCase));
            if (isHeader) { t.Headers.AddRange(cells.Select(c => Text(c.Groups["body"].Value))); continue; }
            var row = new RawRow { Key = (rowIndex++).ToString(CultureInfo.InvariantCulture) };
            foreach (var c in cells)
            {
                row.Cells.Add(Text(c.Groups["body"].Value));
                var h = HrefRx.Match(c.Groups["body"].Value);
                row.Links.Add(h.Success ? WebUtility.HtmlDecode(h.Groups["h"].Value) : "");
            }
            var colspan = Regex.Match(cells[0].Groups["attrs"].Value, @"colspan\s*=\s*[""']?(\d+)", RegexOptions.IgnoreCase);
            row.IsGroup = cells.Count == 1 && (colspan.Success && int.Parse(colspan.Groups[1].Value, CultureInfo.InvariantCulture) > 1 || t.Headers.Count > 1);
            var dataKey = Regex.Match(r.Groups["attrs"].Value, @"data-key\s*=\s*[""'](?<k>[^""']*)[""']", RegexOptions.IgnoreCase);
            if (dataKey.Success) row.Key = dataKey.Groups["k"].Value;
            t.Rows.Add(row);
        }
        // tables without th: use the first row as the header
        if (t.Headers.Count == 0 && t.Rows.Count > 0)
        {
            t.Headers.AddRange(t.Rows[0].Cells);
            t.Rows.RemoveAt(0);
        }
        return t;
    }

    /// <summary>Visible text of a fragment: &lt;br&gt; and block ends become new lines, tags dropped, entities decoded.</summary>
    public static string Text(string fragment)
    {
        var s = Regex.Replace(fragment, @"<(script|style)\b.*?</\1>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        s = Regex.Replace(s, @"<br\s*/?>|</(div|p|li)>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<[^>]+>", "");
        s = WebUtility.HtmlDecode(s);
        var lines = s.Split('\n').Select(l => Regex.Replace(l, @"[ \t\r ]+", " ").Trim()).Where(l => l.Length > 0);
        return string.Join("\n", lines);
    }
}

/// <summary>Turns the Search Workflows results table into steps and works out where the workflow is.</summary>
public static class WorkflowParser
{
    public static WorkflowLookupResult Parse(string workflowNo, RawTable table, AconexConfig cfg, DateTime today)
    {
        var res = new WorkflowLookupResult { WorkflowNo = workflowNo.Trim(), CheckedAt = DateTime.Now };
        var cols = cfg.Workflows.Columns;
        var map = new ColumnMap(table.Headers);
        var ix = new Dictionary<string, int>
        {
            ["DocumentNo"] = map.Find(cols.DocumentNo), ["DocumentRevision"] = map.Find(cols.DocumentRevision), ["DocumentVersion"] = map.Find(cols.DocumentVersion),
            ["DocumentTitle"] = map.Find(cols.DocumentTitle), ["StepName"] = map.Find(cols.StepName), ["Action"] = map.Find(cols.Action),
            ["AssignedTo"] = map.Find(cols.AssignedTo), ["DateIn"] = map.Find(cols.DateIn), ["DateDue"] = map.Find(cols.DateDue),
            ["OriginalDueDate"] = map.Find(cols.OriginalDueDate), ["DateCompleted"] = map.Find(cols.DateCompleted), ["StepStatus"] = map.Find(cols.StepStatus),
            ["StepOutcome"] = map.Find(cols.StepOutcome), ["FileName"] = map.Find(cols.FileName), ["WorkflowNo"] = map.Find(cols.WorkflowNo),
            ["WorkflowName"] = map.Find(cols.WorkflowName),
        };
        // 'Date Due' must not be taken by 'Original Due Date' and 'Status' must not steal 'Step Status' etc. - exact matches came first
        res.MissingColumns = ix.Where(kv => kv.Value < 0 && kv.Key is "StepName" or "StepStatus" or "AssignedTo" or "DateDue").Select(kv => kv.Key).ToList();
        var known = ix.Values.Where(v => v >= 0).ToHashSet();
        res.UnknownHeaders = table.Headers.Where((h, i) => !known.Contains(i) && ColumnMap.Normalize(h).Length > 0).ToList();
        if (ix["StepName"] < 0)
            throw new AconexPageChangedException("The workflow results table has no 'Step Name' column. Headers seen: " + string.Join(" | ", table.Headers) +
                                                 ". Fix Workflows.Columns.StepName in aconex.config.json.");

        var groupRx = new Regex(cfg.Workflows.GroupRowRegex, RegexOptions.IgnoreCase);
        var wanted = NormalizeWf(workflowNo);
        string currentGroup = "";
        var order = 0;
        foreach (var row in table.Rows)
        {
            var whole = string.Join(" ", row.Cells.Select(ColumnMap.Clean));
            if (row.IsGroup || row.Cells.Count <= 2)
            {
                var g = groupRx.Match(whole);
                if (g.Success)
                {
                    currentGroup = g.Groups["no"].Value;
                    if (NormalizeWf(currentGroup) == wanted && g.Groups["name"].Success)
                        res.WorkflowName = Regex.Replace(g.Groups["name"].Value, @"\s+", " ").Trim();
                }
                continue;
            }
            var rowWf = ix["WorkflowNo"] >= 0 ? ColumnMap.Cell(row, ix["WorkflowNo"]) : currentGroup;
            if (rowWf.Length > 0 && NormalizeWf(rowWf) != wanted) continue;   // another workflow in the same result page
            if (ix["WorkflowName"] >= 0 && res.WorkflowName.Length == 0) res.WorkflowName = ColumnMap.Cell(row, ix["WorkflowName"]);
            string C(string k) => ColumnMap.Cell(row, ix[k]);
            DateTime? D(string k) => AconexDates.Parse(C(k), cfg.DateFormats);
            var statusText = C("StepStatus");
            var step = new WorkflowStep
            {
                Order = ++order,
                DocumentNo = C("DocumentNo"), DocumentRevision = C("DocumentRevision"), DocumentVersion = C("DocumentVersion"), DocumentTitle = C("DocumentTitle"),
                StepName = C("StepName").Replace('\n', ' '), Action = C("Action"),
                AssignedTo = string.Join("; ", C("AssignedTo").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
                DateIn = D("DateIn"), DateDue = D("DateDue"), OriginalDueDate = D("OriginalDueDate"), DateCompleted = D("DateCompleted"),
                StepStatusText = statusText, StepStatus = NormalizeStatus(statusText),
                StepOutcome = C("StepOutcome").Replace('\n', ' '), FileName = C("FileName").Replace('\n', ' '),
            };
            if (step.StepName.Length == 0) continue;
            res.Steps.Add(step);
        }
        Analyze(res, cfg, today);
        return res;
    }

    public static string NormalizeWf(string? wf) => Regex.Replace((wf ?? "").ToUpperInvariant(), @"[^A-Z0-9]", "");

    public static string NormalizeStatus(string? s)
    {
        var t = (s ?? "").Trim().ToLowerInvariant();
        if (t.Length == 0) return StepStatuses.Unknown;
        if (t.Contains("overdue")) return StepStatuses.Overdue;
        if (t.Contains("complete") || t.Contains("closed") || t.Contains("done")) return StepStatuses.Completed;
        if (t.Contains("terminat") || t.Contains("cancel")) return StepStatuses.Terminated;
        if (t.Contains("pending") || t.Contains("in progress") || t.Contains("open") || t.Contains("current")) return StepStatuses.Pending;
        return StepStatuses.Unknown;
    }

    /// <summary>Current step = earliest open step (by Date In, then table order); outcome = rejection anywhere, else approval of the last step.</summary>
    public static void Analyze(WorkflowLookupResult res, AconexConfig cfg, DateTime today)
    {
        if (res.Steps.Count == 0)
        {
            res.State = WorkflowStates.NotFound;
            return;
        }
        var first = res.Steps[0];
        res.DocumentNo = first.DocumentNo;
        res.DocumentTitle = first.DocumentTitle;
        bool Matches(string outcome, IEnumerable<string> keys) => keys.Any(k => outcome.Contains(k, StringComparison.OrdinalIgnoreCase));

        var open = res.Steps.Where(s => s.StepStatus is StepStatuses.Pending or StepStatuses.Overdue
                                        || s.StepStatus == StepStatuses.Unknown && s.DateCompleted is null)
                            .OrderBy(s => s.DateIn ?? DateTime.MaxValue).ThenBy(s => s.Order).ToList();
        var rejected = res.Steps.Where(s => s.StepStatus == StepStatuses.Completed && Matches(s.StepOutcome, cfg.Workflows.RejectedOutcomes)).ToList();

        if (open.Count > 0)
        {
            var cur = open[0];
            res.CurrentStep = cur.StepName;
            res.WithWhom = string.Join("; ", open.Where(s => s.StepName == cur.StepName).Select(s => s.AssignedTo).Distinct());
            res.DateDue = open.Where(s => s.StepName == cur.StepName).Select(s => s.DateDue).Where(d => d.HasValue).DefaultIfEmpty().Min();
            res.IsOverdue = open.Any(s => s.StepStatus == StepStatuses.Overdue) || res.DateDue is { } due && due.Date < today.Date;
            res.DaysOverdue = res.IsOverdue && res.DateDue is { } d2 ? Math.Max(0, (int)(today.Date - d2.Date).TotalDays) : 0;
            res.Outcome = rejected.Count > 0 ? rejected[^1].StepOutcome : cur.StepOutcome.Length > 0 ? cur.StepOutcome : "Pending";
            res.State = rejected.Count > 0 ? WorkflowStates.Rejected : res.IsOverdue ? WorkflowStates.Overdue : WorkflowStates.InProgress;
            return;
        }
        var last = res.Steps.Where(s => s.DateCompleted.HasValue).OrderBy(s => s.DateCompleted).ThenBy(s => s.Order).LastOrDefault() ?? res.Steps[^1];
        res.CurrentStep = "(complete)";
        res.WithWhom = "";
        res.Outcome = rejected.Count > 0 ? rejected[^1].StepOutcome : last.StepOutcome;
        res.State = rejected.Count > 0 ? WorkflowStates.Rejected
            : Matches(res.Outcome, cfg.Workflows.ApprovedOutcomes) || res.Steps.All(s => s.StepStatus == StepStatuses.Completed) ? WorkflowStates.Approved
            : WorkflowStates.InProgress;
    }
}

/// <summary>Document register helpers: parse result rows, read number lists, route to folders.</summary>
public static class DocumentRegister
{
    public static string RevisionKey(string docNo, string rev) => $"{(docNo ?? "").Trim().ToUpperInvariant()}|{(rev ?? "").Trim().ToUpperInvariant()}";

    public static List<DocumentHit> ParseResults(RawTable table, AconexConfig cfg, int page = 1)
    {
        var c = cfg.Documents.Columns;
        var map = new ColumnMap(table.Headers);
        var iNo = map.Find(c.DocumentNo);
        if (iNo < 0)
            throw new AconexPageChangedException("The document register table has no 'Document No' column. Headers seen: " + string.Join(" | ", table.Headers) +
                                                 ". Fix Documents.Columns.DocumentNo in aconex.config.json.");
        int iRev = map.Find(c.Revision), iVer = map.Find(c.Version), iTitle = map.Find(c.Title), iType = map.Find(c.Type), iDisc = map.Find(c.Discipline),
            iDate = map.Find(c.Date), iStatus = map.Find(c.Status), iFile = map.Find(c.FileName), iGroup = map.Find(c.Group);
        var list = new List<DocumentHit>();
        foreach (var r in table.Rows)
        {
            if (r.IsGroup) continue;
            var no = ColumnMap.Cell(r, iNo);
            if (no.Length == 0) continue;
            list.Add(new DocumentHit
            {
                DocumentNo = no.Split('\n')[0].Trim(), Revision = ColumnMap.Cell(r, iRev), Version = ColumnMap.Cell(r, iVer), Title = ColumnMap.Cell(r, iTitle).Replace('\n', ' '),
                Type = ColumnMap.Cell(r, iType), Discipline = ColumnMap.Cell(r, iDisc), Group = ColumnMap.Cell(r, iGroup),
                Date = AconexDates.Parse(ColumnMap.Cell(r, iDate), cfg.DateFormats), Status = ColumnMap.Cell(r, iStatus), FileName = ColumnMap.Cell(r, iFile),
                RowKey = r.Key, Page = page,
            });
        }
        return list;
    }

    /// <summary>Document numbers in pasted text (one per line, or anything matching the configured pattern).</summary>
    public static List<string> ParseNumberList(string text, string? pattern = null)
    {
        var rx = new Regex(pattern ?? new DocumentRegisterConfig().DocNumberRegex, RegexOptions.IgnoreCase);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (text ?? "").Split(new[] { '\n', '\r', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var ms = rx.Matches(line.ToUpperInvariant());
            var found = ms.Count > 0 ? ms.Select(m => m.Value) : new[] { line.ToUpperInvariant() };
            foreach (var f in found)
                if (f.Any(char.IsDigit) && seen.Add(f)) result.Add(f);
        }
        return result;
    }

    /// <summary>Numbers from the first column that looks like document numbers in an Excel / CSV sheet.</summary>
    public static List<string> ReadNumbersFromFile(string path, string? pattern = null)
    {
        var t = Import.TableReader.Read(path);
        var header = t.Headers.FirstOrDefault(h => h.Contains("DOC", StringComparison.OrdinalIgnoreCase) || h.Contains("NUMBER", StringComparison.OrdinalIgnoreCase) || h.Contains("NO", StringComparison.OrdinalIgnoreCase))
                     ?? t.Headers.FirstOrDefault();
        var sb = new StringBuilder();
        if (header != null)
        {
            // TableReader treats the first row as the header - it may itself be a number
            if (Regex.IsMatch(header, pattern ?? new DocumentRegisterConfig().DocNumberRegex, RegexOptions.IgnoreCase)) sb.AppendLine(header);
            foreach (var r in t.Rows) sb.AppendLine(r.Get(header));
        }
        return ParseNumberList(sb.ToString(), pattern);
    }

    public static string Kind(DocumentHit hit, FolderConfig f)
    {
        bool Is(IEnumerable<string> keys) => keys.Any(k => hit.Type.Equals(k, StringComparison.OrdinalIgnoreCase) || hit.Type.Contains(k, StringComparison.OrdinalIgnoreCase));
        if (Is(f.MirTypes) || Regex.IsMatch(hit.DocumentNo, @"(^|-)MIR(-|$)", RegexOptions.IgnoreCase)) return "MIR";
        if (Is(f.WirTypes) || Regex.IsMatch(hit.DocumentNo, @"(^|-)WIR(-|$)", RegexOptions.IgnoreCase)) return "WIR";
        return "OTHER";
    }

    /// <summary>Target folder for a hit: WIR / MIR / other root + optional sub-folder template ({yyyy-MM}, {type}, {discipline}).</summary>
    public static string TargetFolder(DocumentHit hit, FolderConfig f)
    {
        var kind = Kind(hit, f);
        var root = AconexConfig.Expand(kind switch { "WIR" => f.WirFolder, "MIR" => f.MirFolder, _ => f.OtherFolder });
        if (string.IsNullOrWhiteSpace(root)) root = AconexConfig.Expand(f.OtherFolder);
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException($"No download folder set for {kind}. Set Folders.{(kind == "OTHER" ? "Other" : kind[..1] + kind[1..].ToLowerInvariant())}Folder.");
        var sub = f.SubFolderTemplate ?? "";
        if (sub.Length > 0)
        {
            var d = hit.Date ?? DateTime.Today;
            sub = Regex.Replace(sub, @"\{(yyyy-MM|yyyy|MM)\}", m => d.ToString(m.Groups[1].Value, CultureInfo.InvariantCulture));
            sub = sub.Replace("{type}", Safe(kind)).Replace("{discipline}", Safe(hit.Discipline.Length > 0 ? hit.Discipline : "NA"));
            return Path.Combine(root, sub);
        }
        return root;
    }

    public static string Safe(string name)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
        var s = new string((name ?? "").Select(ch => bad.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return s.Length == 0 ? "_" : s;
    }
}
