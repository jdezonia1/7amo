using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Raffaello.Core.AconexWeb;

namespace Raffaello.Core.Integrations.AconexApi;

/// <summary>A project the API user can see.</summary>
public sealed record AconexProject(string ProjectId, string Name, string ShortName);

/// <summary>
/// <see cref="IAconexClient"/> over the Oracle Aconex REST API (XML) instead of browser automation: same workflow lookup and
/// register search / download contract, so the status board, downloads queue and invoice links work unchanged.
/// Endpoints (Aconex API reference): GET /api/projects; GET /api/projects/{id}/register?search_query=...&amp;return_fields=...
/// &amp;search_type=PAGED&amp;page_number=&amp;page_size=; GET /api/projects/{id}/register/{documentId}/file;
/// GET /api/projects/{id}/workflows/search?search_query=workflow_number:"WF-..." (one result per step).
/// "Screenshots" are replaced by a PNG evidence card of the steps (rendered locally) plus the raw XML answer.
/// </summary>
public sealed class AconexApiClient : IAconexClient
{
    private readonly AconexApiConfig _cfg;
    private readonly AconexConfig _web;
    private readonly HttpClient _http;
    private readonly Dictionary<string, string> _docIds = new(StringComparer.OrdinalIgnoreCase);
    private string? _token;
    private DateTime _tokenExpires;
    public Func<DateTime> Today { get; set; } = () => DateTime.Today;

    public event Action<string>? Log;

    /// <param name="web">The browser config: only its outcome keywords, date formats and folders are used.</param>
    public AconexApiClient(AconexApiConfig cfg, AconexConfig? web = null, HttpMessageHandler? handler = null)
    {
        _cfg = cfg;
        _web = web ?? new AconexConfig();
        _http = new HttpClient(handler ?? new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            BaseAddress = new Uri(cfg.BaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(Math.Max(10, cfg.TimeoutSeconds)),
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.aconex.project.v1+xml"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.1));
        if (cfg.ApplicationKey.Length > 0) _http.DefaultRequestHeaders.TryAddWithoutValidation(cfg.ApplicationKeyHeader, cfg.ApplicationKey);
    }

    public ValueTask DisposeAsync() { _http.Dispose(); return ValueTask.CompletedTask; }

    // ------------------------------------------------------------------ auth + transport

    private string Path(string template, string documentId = "") =>
        template.Replace("{projectId}", Uri.EscapeDataString(_cfg.ProjectId)).Replace("{documentId}", Uri.EscapeDataString(documentId)).TrimStart('/');

    private async Task<AuthenticationHeaderValue> AuthAsync(CancellationToken ct)
    {
        switch (_cfg.AuthMode)
        {
            case AconexAuthModes.Basic:
                return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_cfg.UserName}:{_cfg.Secret()}")));
            case AconexAuthModes.Bearer:
                return new AuthenticationHeaderValue("Bearer", _cfg.Secret());
            default:
                if (_token != null && DateTime.UtcNow < _tokenExpires) return new AuthenticationHeaderValue("Bearer", _token);
                using (var req = new HttpRequestMessage(HttpMethod.Post, _cfg.TokenUrl))
                {
                    var form = new List<KeyValuePair<string, string>> { new("grant_type", "client_credentials") };
                    if (_cfg.Scope.Length > 0) form.Add(new("scope", _cfg.Scope));
                    req.Content = new FormUrlEncodedContent(form);
                    req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(_cfg.ClientId)}:{Uri.EscapeDataString(_cfg.Secret())}")));
                    using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                    var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        throw new AconexLoginRequiredException($"Aconex API sign-in refused ({(int)resp.StatusCode}): check ClientId / secret / TokenUrl in aconex-api.json. {Trim(body)}");
                    using var doc = JsonDocument.Parse(body);
                    _token = doc.RootElement.GetProperty("access_token").GetString();
                    var expires = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3600;
                    _tokenExpires = DateTime.UtcNow.AddSeconds(Math.Max(60, expires - 60));
                }
                return new AuthenticationHeaderValue("Bearer", _token!);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var req = make();
            req.Headers.Authorization = await AuthAsync(ct).ConfigureAwait(false);
            var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return resp;
            var status = (int)resp.StatusCode;
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            resp.Dispose();
            if (status == 401 && attempt == 1 && _cfg.AuthMode == AconexAuthModes.OAuthClientCredentials) { _token = null; continue; }
            if (status is 401 or 403) throw new AconexLoginRequiredException($"Aconex API refused access ({status}) - the integration user needs access to project {_cfg.ProjectId}. {Trim(body)}");
            if ((status == 429 || status >= 500) && attempt < Math.Max(1, _cfg.MaxRetries))
            {
                var wait = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
                Log?.Invoke($"Aconex API busy ({status}), retrying in {wait.TotalSeconds:0} s");
                await Task.Delay(wait, ct).ConfigureAwait(false);
                continue;
            }
            throw new HttpRequestException($"Aconex API {status} for {make().RequestUri}: {Trim(body)}", null, (HttpStatusCode)status);
        }
    }

    private async Task<XDocument> GetXmlAsync(string relative, CancellationToken ct)
    {
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, relative), ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (_cfg.DelayBetweenCallsMs > 0) await Task.Delay(_cfg.DelayBetweenCallsMs, ct).ConfigureAwait(false);
        try { return XDocument.Parse(text); }
        catch (System.Xml.XmlException ex) { throw new AconexPageChangedException($"Aconex API answered something that is not XML ({ex.Message}): {Trim(text)}"); }
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] + "..." : s;

    // ------------------------------------------------------------------ projects

    public async Task<List<AconexProject>> ProjectsAsync(CancellationToken ct = default)
    {
        var x = await GetXmlAsync(_cfg.Paths.Projects.TrimStart('/'), ct).ConfigureAwait(false);
        return x.Descendants().Where(e => e.Name.LocalName == "Project")
            .Select(p => new AconexProject(V(p, "ProjectId"), V(p, "ProjectName"), V(p, "ProjectShortName"))).ToList();
    }

    public async Task EnsureLoggedInAsync(CancellationToken ct = default)
    {
        var projects = await ProjectsAsync(ct).ConfigureAwait(false);
        if (_cfg.ProjectId.Length > 0 && !projects.Any(p => p.ProjectId == _cfg.ProjectId))
            throw new AconexLoginRequiredException($"The API user cannot see project {_cfg.ProjectId}. Projects visible: {string.Join(", ", projects.Select(p => $"{p.ProjectId} {p.Name}"))}.");
        Log?.Invoke($"Aconex API: signed in, {projects.Count} project(s) visible.");
    }

    // ------------------------------------------------------------------ workflows

    public async Task<WorkflowLookupResult> LookupWorkflowAsync(string workflowNo, string screenshotFolder, CancellationToken ct = default)
    {
        var q = $"{_cfg.Fields.WorkflowNo}:\"{workflowNo.Trim()}\"";
        var rel = $"{Path(_cfg.Paths.WorkflowSearch)}?search_query={Uri.EscapeDataString(q)}&page_size={_cfg.PageSize}";
        var x = await GetXmlAsync(rel, ct).ConfigureAwait(false);
        var res = ParseWorkflow(workflowNo, x, _web, Today());
        if (!string.IsNullOrEmpty(screenshotFolder))
        {
            Directory.CreateDirectory(screenshotFolder);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var safe = DocumentRegister.Safe(workflowNo);
            var xml = System.IO.Path.Combine(screenshotFolder, $"{safe}_{stamp}_api.xml");
            await File.WriteAllTextAsync(xml, x.ToString(), ct).ConfigureAwait(false);
            res.PageScreenshotPath = xml;
            if (res.Steps.Count > 0)
            {
                var png = System.IO.Path.Combine(screenshotFolder, $"{safe}_{stamp}_steps.png");
                await File.WriteAllBytesAsync(png, EvidenceCard(res), ct).ConfigureAwait(false);
                res.TableScreenshotPath = png;
            }
        }
        Log?.Invoke(res.Summary);
        return res;
    }

    /// <summary>Workflow search answer (one &lt;Workflow&gt; element per step) -&gt; lookup result, analysed like the web table.</summary>
    public static WorkflowLookupResult ParseWorkflow(string workflowNo, XDocument x, AconexConfig web, DateTime today)
    {
        var res = new WorkflowLookupResult { WorkflowNo = workflowNo.Trim(), CheckedAt = DateTime.Now };
        var wanted = WorkflowParser.NormalizeWf(workflowNo);
        var order = 0;
        foreach (var w in x.Descendants().Where(e => e.Name.LocalName == "Workflow"))
        {
            var no = V(w, "WorkflowNumber");
            if (no.Length > 0 && WorkflowParser.NormalizeWf(no) != wanted) continue;
            if (res.WorkflowName.Length == 0) res.WorkflowName = V(w, "WorkflowName");
            var assignees = w.Descendants().Where(e => e.Name.LocalName == "Assignee")
                .Select(a => string.Join(" - ", new[] { V(a, "Name"), V(a, "OrganizationName") }.Where(s => s.Length > 0))).Where(s => s.Length > 0).ToList();
            var statusText = V(w, "StepStatus");
            var step = new WorkflowStep
            {
                Order = ++order, DocumentNo = V(w, "DocumentNumber"), DocumentRevision = V(w, "DocumentRevision"), DocumentVersion = V(w, "DocumentVersion"),
                DocumentTitle = V(w, "DocumentTitle"), StepName = V(w, "StepName"), Action = V(w, "Reasons"),
                AssignedTo = assignees.Count > 0 ? string.Join("; ", assignees) : V(w, "Assignees"),
                DateIn = D(V(w, "DateIn"), web), DateDue = D(V(w, "DateDue"), web), OriginalDueDate = D(V(w, "OriginalDueDate"), web),
                DateCompleted = D(V(w, "DateCompleted"), web), StepStatusText = statusText, StepStatus = WorkflowParser.NormalizeStatus(statusText),
                StepOutcome = V(w, "StepOutcome"), FileName = V(w, "FileName"),
            };
            if (step.StepName.Length == 0) continue;
            res.Steps.Add(step);
        }
        WorkflowParser.Analyze(res, web, today);
        return res;
    }

    // ------------------------------------------------------------------ document register

    public async Task<List<DocumentHit>> SearchDocumentsAsync(DocumentQuery query, CancellationToken ct = default)
    {
        var hits = new List<DocumentHit>();
        var queries = BuildQueries(query, _cfg.Fields);
        foreach (var q in queries)
        {
            var page = 1;
            while (true)
            {
                var rel = $"{Path(_cfg.Paths.RegisterSearch)}?search_query={Uri.EscapeDataString(q)}&return_fields={Uri.EscapeDataString(_cfg.Fields.ReturnFields)}" +
                          $"&search_type=PAGED&page_number={page}&page_size={_cfg.PageSize}";
                var x = await GetXmlAsync(rel, ct).ConfigureAwait(false);
                var found = ParseRegister(x, _web, page);
                foreach (var h in found)
                {
                    var id = h.RowKey.StartsWith("api:", StringComparison.Ordinal) ? h.RowKey[4..] : "";
                    if (id.Length > 0) _docIds[DocumentRegister.RevisionKey(h.DocumentNo, h.Revision)] = id;
                }
                hits.AddRange(found);
                var root = x.Root;
                var totalPages = int.TryParse(root?.Attribute("TotalPages")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tp) ? tp : 1;
                if (page >= totalPages || found.Count == 0) break;
                page++;
            }
        }
        Log?.Invoke($"Aconex API register: {hits.Count} document revision(s) for {query.Describe()}");
        return hits;
    }

    /// <summary>
    /// search_query texts for a query. Numbers go in chunks of 40 (docno:"A" OR docno:"B" ...); the other criteria are ANDed.
    /// An empty query is refused (it would download the whole register).
    /// </summary>
    public static List<string> BuildQueries(DocumentQuery q, AconexApiFields f)
    {
        var common = new List<string>();
        if (q.DateFrom != null || q.DateTo != null)
            common.Add($"{f.Date}:[{(q.DateFrom ?? new DateTime(2000, 1, 1)).ToString(f.DateFormat, CultureInfo.InvariantCulture)} TO {(q.DateTo ?? new DateTime(2100, 1, 1)).ToString(f.DateFormat, CultureInfo.InvariantCulture)}]");
        if (q.Group.Length > 0) common.Add($"{f.Group}:\"{Esc(q.Group)}\"");
        if (q.Discipline.Length > 0) common.Add($"{f.Discipline}:\"{Esc(q.Discipline)}\"");
        if (q.DocType.Length > 0) common.Add($"{f.DocType}:\"{Esc(q.DocType)}\"");
        if (q.DocumentNumbers.Count == 0)
        {
            if (common.Count == 0) throw new ArgumentException("Give document numbers, a date range, a group, a discipline or a type - an empty search would list the whole register.");
            return new List<string> { string.Join(" AND ", common) };
        }
        var list = new List<string>();
        foreach (var chunk in q.DocumentNumbers.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Chunk(40))
        {
            var nos = "(" + string.Join(" OR ", chunk.Select(n => $"{f.DocNo}:\"{Esc(n)}\"")) + ")";
            list.Add(common.Count == 0 ? nos : nos + " AND " + string.Join(" AND ", common));
        }
        return list;
    }

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    public static List<DocumentHit> ParseRegister(XDocument x, AconexConfig web, int page = 1) =>
        x.Descendants().Where(e => e.Name.LocalName == "Document").Select(d => new DocumentHit
        {
            DocumentNo = V(d, "DocumentNumber"), Revision = V(d, "Revision"), Version = V(d, "VersionNumber"), Title = V(d, "Title"),
            Type = V(d, "DocumentType"), Discipline = V(d, "Discipline"), Group = V(d, "Category"),
            Date = D(FirstNonEmpty(V(d, "DateRegistered"), V(d, "Registered"), V(d, "DateModified")), web),
            Status = FirstNonEmpty(V(d, "DocumentStatus"), V(d, "Status")), FileName = FirstNonEmpty(V(d, "Filename"), V(d, "FileName")),
            RowKey = "api:" + (d.Attribute("DocumentId")?.Value ?? V(d, "DocumentId")), Page = page,
        }).Where(h => h.DocumentNo.Length > 0).ToList();

    public async Task<string> DownloadAsync(DocumentHit hit, DocumentQuery query, string targetFolder, CancellationToken ct = default)
    {
        var id = hit.RowKey.StartsWith("api:", StringComparison.Ordinal) && hit.RowKey.Length > 4 ? hit.RowKey[4..] : "";
        if (id.Length == 0 && !_docIds.TryGetValue(DocumentRegister.RevisionKey(hit.DocumentNo, hit.Revision), out id!))
        {
            // the queue was planned in an earlier session: find the document id again by number
            var again = await SearchDocumentsAsync(new DocumentQuery { DocumentNumbers = new() { hit.DocumentNo } }, ct).ConfigureAwait(false);
            var match = again.FirstOrDefault(h => h.Revision.Equals(hit.Revision, StringComparison.OrdinalIgnoreCase)) ?? again.FirstOrDefault();
            id = match is null ? "" : match.RowKey[4..];
            if (id.Length == 0) throw new FileNotFoundException($"{hit.DocumentNo} rev {hit.Revision} is not in the Aconex register (API).");
        }
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Path(_cfg.Paths.DocumentFile, id)), ct).ConfigureAwait(false);
        var name = resp.Content.Headers.ContentDisposition?.FileNameStar ?? resp.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        if (string.IsNullOrWhiteSpace(name)) name = hit.FileName.Length > 0 ? hit.FileName : $"{hit.DocumentNo}_{hit.Revision}.pdf";
        name = DocumentRegister.Safe(System.IO.Path.GetFileName(name));
        Directory.CreateDirectory(targetFolder);
        var path = ZipBundle.UniquePath(System.IO.Path.Combine(targetFolder, name));
        await using (var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var f = File.Create(path))
            await s.CopyToAsync(f, ct).ConfigureAwait(false);
        if (_cfg.DelayBetweenCallsMs > 0) await Task.Delay(_cfg.DelayBetweenCallsMs, ct).ConfigureAwait(false);
        Log?.Invoke($"{hit.DocumentNo} rev {hit.Revision}: {name} ({new FileInfo(path).Length:N0} bytes)");
        return path;
    }

    // ------------------------------------------------------------------ helpers

    private static string V(XElement e, string name)
    {
        var c = e.Elements().FirstOrDefault(x => x.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (c != null) return c.Value.Trim();
        var a = e.Attributes().FirstOrDefault(x => x.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
        return a?.Value.Trim() ?? "";
    }

    private static string FirstNonEmpty(params string[] v) => v.FirstOrDefault(s => s.Length > 0) ?? "";

    private static DateTime? D(string text, AconexConfig web)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // ISO 8601 (the API's format, often UTC "...Z"): shown in local time like the web pages
        if (text.Length >= 10 && char.IsDigit(text[0]) && text[4] == '-' && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var iso))
            return iso.Kind == DateTimeKind.Utc ? iso.ToLocalTime() : iso;
        return AconexDates.Parse(text, web.DateFormats);
    }

    /// <summary>PNG "screenshot" of the workflow steps (the API has no page to photograph).</summary>
    public static byte[] EvidenceCard(WorkflowLookupResult r)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var images = Document.Create(doc => doc.Page(page =>
        {
            page.Size(new PageSize(842, 120 + 18 * Math.Min(r.Steps.Count, 40)));
            page.Margin(14);
            page.DefaultTextStyle(t => t.FontSize(7));
            page.Content().Column(col =>
            {
                col.Item().Text($"ACONEX API - {r.WorkflowNo} {r.WorkflowName}").FontSize(11).Bold().FontColor("#8B0000");
                col.Item().Text($"{r.State}  -  {r.Summary}  -  checked {r.CheckedAt:dd-MMM-yyyy HH:mm}").FontSize(8);
                col.Item().PaddingTop(4).Table(t =>
                {
                    t.ColumnsDefinition(c => { c.ConstantColumn(18); c.RelativeColumn(3); c.RelativeColumn(3); c.RelativeColumn(2); c.RelativeColumn(2); c.RelativeColumn(2); c.RelativeColumn(2); c.RelativeColumn(2); });
                    foreach (var h in new[] { "#", "STEP", "ASSIGNED TO", "IN", "DUE", "COMPLETED", "STATUS", "OUTCOME" })
                        t.Cell().Background("#A6A6A6").Padding(2).Text(h).Bold();
                    foreach (var s in r.Steps.Take(40))
                    {
                        t.Cell().Padding(2).Text(s.Order.ToString(CultureInfo.InvariantCulture));
                        t.Cell().Padding(2).Text(s.StepName);
                        t.Cell().Padding(2).Text(s.AssignedTo);
                        t.Cell().Padding(2).Text(s.DateIn?.ToString("dd-MMM-yy", CultureInfo.InvariantCulture) ?? "");
                        t.Cell().Padding(2).Text(s.DateDue?.ToString("dd-MMM-yy", CultureInfo.InvariantCulture) ?? "");
                        t.Cell().Padding(2).Text(s.DateCompleted?.ToString("dd-MMM-yy", CultureInfo.InvariantCulture) ?? "");
                        t.Cell().Padding(2).Text(s.StepStatusText);
                        t.Cell().Padding(2).Text(s.StepOutcome);
                    }
                });
            });
        })).GenerateImages(new ImageGenerationSettings { ImageFormat = ImageFormat.Png, RasterDpi = 110 });
        return images.First();
    }
}
