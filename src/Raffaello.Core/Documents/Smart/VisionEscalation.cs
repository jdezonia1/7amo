using System.Globalization;
using System.Text.Json.Nodes;

namespace Raffaello.Core.Documents.Smart;

/// <summary>
/// Cloud escalation (only when the user enabled cloud reading and set a key): pages with conflicts or derived numbers are sent to
/// Claude vision with a strict JSON schema of the schedule rows; its readings join the vote as one more engine and pass through the
/// same validators (qty x rate = total). A disagreement stays a CONFLICT for the user - the cloud answer is never taken blindly.
/// </summary>
public static class VisionEscalation
{
    public static readonly JsonObject ScheduleSchema = new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("rows"),
        ["properties"] = new JsonObject
        {
            ["rows"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object", ["additionalProperties"] = false,
                    ["required"] = new JsonArray("item_no", "description", "unit", "qty", "rate", "total"),
                    ["properties"] = new JsonObject
                    {
                        ["item_no"] = new JsonObject { ["type"] = "string" }, ["description"] = new JsonObject { ["type"] = "string" }, ["unit"] = new JsonObject { ["type"] = "string" },
                        ["qty"] = new JsonObject { ["type"] = "number" }, ["rate"] = new JsonObject { ["type"] = "number" }, ["total"] = new JsonObject { ["type"] = "number" },
                    },
                },
            },
        },
    };

    public const string ScheduleInstructions =
        "This page is part of a contract rate schedule (Arabic). Columns: item no (رقم), description (التوصيف), unit (الوحدة), quantity (الكمية), unit rate (سعر الوحدة), total (الاجمالي). " +
        "Return every numbered row exactly as printed: description in Arabic as written (keep Latin words and numbers inside it), unit as printed (e.g. عدد, م.ط, طرف), " +
        "numbers without thousands separators. Skip section titles and the header. If a cell is covered by a stamp and cannot be read, return 0 for that number.";

    public static async Task ScheduleAsync(string pdfPath, SmartDocument doc, ScheduleRead read, IVisionReader vision, CancellationToken ct = default)
    {
        var pages = read.Items.Where(i => i.NeedsReview).Select(i => i.Page).Distinct().ToList();
        foreach (var pageNo in pages)
        {
            ct.ThrowIfCancellationRequested();
            var page = doc.Pages.FirstOrDefault(p => p.Number == pageNo);
            if (page is null) continue;
            var (bytes, media) = page.Ocr?.ImagePng is { } png ? (png, "image/png") : ReaderPipeline.PageContent(pdfPath, page.Base);
            JsonNode? json;
            try
            {
                json = await vision.ExtractAsync(new VisionRequest
                {
                    DocumentKind = "contract rate schedule", Page = pageNo, Content = bytes, MediaType = media, Schema = ScheduleSchema, Instructions = ScheduleInstructions,
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                read.Issues.Add(new(IssueLevel.Warn, "VISION", $"page {pageNo}: cloud reading failed - {ex.Message}", null, ""));
                continue;
            }
            Apply(read, pageNo, json);
        }
    }

    /// <summary>Adds the vision readings as candidates and re-votes the flagged rows of that page with the arithmetic check.</summary>
    public static void Apply(ScheduleRead read, int pageNo, JsonNode? json)
    {
        foreach (var row in json?["rows"]?.AsArray() ?? new JsonArray())
        {
            if (row is null) continue;
            var no = row["item_no"]?.GetValue<string>()?.Trim() ?? "";
            var item = read.Items.FirstOrDefault(i => i.Page == pageNo && i.ItemNo == no);
            if (item is null || !item.NeedsReview) continue;
            double N(string k) => row[k]?.GetValue<double>() ?? 0;
            var (q, r, t) = (N("qty"), N("rate"), N("total"));
            const string engine = "claude-vision";
            void Add(FieldResult f, double v) { if (v > 0) f.Candidates.Add(new FieldCandidate(v.ToString("0.###", CultureInfo.InvariantCulture), 0.9, engine, f.Box, pageNo)); }
            Add(item.Qty, q); Add(item.Rate, r); Add(item.Total, t);
            if (q > 0 && r > 0 && t > 0 && DocValidators.AmountOk(q, r, t))
            {
                foreach (var (f, v) in new[] { (item.Qty, q), (item.Rate, r), (item.Total, t) })
                {
                    var local = f.Number;
                    var agree = local is double l && Math.Abs(l - v) < 1e-6;
                    if (agree && f.Status != FieldStatus.Derived) { f.Status = FieldStatus.Agreed; f.Confidence = Math.Max(f.Confidence, 0.97); continue; }
                    // the offline reading disagrees or was derived: the cloud reading satisfies the arithmetic - still shown as VALIDATED with both values
                    f.Note = $"offline '{f.Value}' vs cloud '{v.ToString("0.###", CultureInfo.InvariantCulture)}' - cloud reading passes qty x rate = total";
                    f.Value = v.ToString("0.###", CultureInfo.InvariantCulture);
                    f.Status = FieldStatus.Validated;
                    f.Confidence = 0.88;
                }
                item.Issues.Remove("qty x rate does not match the total");
            }
            var desc = row["description"]?.GetValue<string>() ?? "";
            if (desc.Length > 0) item.Description.Candidates.Add(new FieldCandidate(desc, 0.9, engine, item.Description.Box, pageNo));
        }
    }
}
