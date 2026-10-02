using System.Text.Json;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Documents;

/// <summary>
/// A document that was read: identity (SHA-256), type, issuer, where the evidence file is kept and which record it fed.
/// The data itself lives in the module tables (contract items, DN lines ...); this row is the evidence index.
/// </summary>
public sealed class DocRecord : Entity
{
    public string FileName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Bytes { get; set; }
    public int Pages { get; set; }
    /// <summary>Main type (<see cref="Smart.DocTypes"/>); bundles keep their segments in <see cref="Segments"/>.</summary>
    public string DocType { get; set; } = "";
    public string Segments { get; set; } = "";
    public string Issuer { get; set; } = "";
    /// <summary>Evidence copy (shared documents folder / server document id).</summary>
    public string StoredPath { get; set; } = "";
    public long ServerDocumentId { get; set; }
    /// <summary>The record the document fed: "Contract" + contract no, "MatDn" + DN no ...</summary>
    public string LinkedTable { get; set; } = "";
    public string LinkedKey { get; set; } = "";
    public string Engines { get; set; } = "";
    /// <summary>DRAFT (read, not confirmed) / CONFIRMED.</summary>
    public string Status { get; set; } = "DRAFT";
    public double Confidence { get; set; }
    public DateTime ReadAt { get; set; }
    public string Notes { get; set; } = "";
}

/// <summary>The text of one page (search index + evidence), with its source and quality.</summary>
public sealed class DocPageText : Entity
{
    public long DocRecordId { get; set; }
    public int Page { get; set; }
    public string Kind { get; set; } = "";
    public string Source { get; set; } = "";
    public string Engine { get; set; } = "";
    public double Quality { get; set; }
    public double Confidence { get; set; }
    public string Text { get; set; } = "";
    /// <summary>Normalised text (digits, Arabic variants) - what the full-text index matches.</summary>
    public string NormText { get; set; } = "";
}

/// <summary>One extracted field with its box on the page and its status (review screen; the confirmed value goes to the module table).</summary>
public sealed class DocField : Entity
{
    public long DocRecordId { get; set; }
    public int Page { get; set; }
    /// <summary>Row of a table ("item 33"), empty for header fields.</summary>
    public string RowKey { get; set; } = "";
    public string Field { get; set; } = "";
    public string Value { get; set; } = "";
    public double Confidence { get; set; }
    public string Status { get; set; } = "";
    /// <summary>Box as fractions of the page (0..1).</summary>
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public string Alternatives { get; set; } = "";
    public string Note { get; set; } = "";
}

/// <summary>
/// A learned layout for (document type, issuer): anchors that identify the issuer, header field zones and table column ranges
/// (fractions of the page). Stored when the user confirms an extraction; the next document from the same issuer is read with it.
/// </summary>
public sealed class ReadTemplate : Entity
{
    public string DocType { get; set; } = "";
    public string Issuer { get; set; } = "";
    /// <summary>Text anchors (normalised) that must appear on the page, '|' separated.</summary>
    public string Anchors { get; set; } = "";
    /// <summary>JSON: role -> [x0, x1] (fractions of the page width).</summary>
    public string ColumnsJson { get; set; } = "";
    /// <summary>JSON: field -> [x, y, w, h] (fractions of the page).</summary>
    public string ZonesJson { get; set; } = "";
    public int Confirmations { get; set; }
    public int TimesUsed { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public string Notes { get; set; } = "";

    public Dictionary<string, (double X0, double X1)> ColumnRanges()
    {
        var res = new Dictionary<string, (double, double)>();
        if (string.IsNullOrWhiteSpace(ColumnsJson)) return res;
        try
        {
            foreach (var kv in JsonSerializer.Deserialize<Dictionary<string, double[]>>(ColumnsJson) ?? new())
                if (kv.Value.Length == 2) res[kv.Key] = (kv.Value[0], kv.Value[1]);
        }
        catch (JsonException) { }
        return res;
    }

    public void SetColumns(IReadOnlyDictionary<string, (double X0, double X1)> cols) =>
        ColumnsJson = JsonSerializer.Serialize(cols.ToDictionary(k => k.Key, k => new[] { Math.Round(k.Value.X0, 4), Math.Round(k.Value.X1, 4) }));

    public Dictionary<string, Ocr.Box> Zones()
    {
        var res = new Dictionary<string, Ocr.Box>();
        if (string.IsNullOrWhiteSpace(ZonesJson)) return res;
        try
        {
            foreach (var kv in JsonSerializer.Deserialize<Dictionary<string, double[]>>(ZonesJson) ?? new())
                if (kv.Value.Length == 4) res[kv.Key] = new Ocr.Box(kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3]);
        }
        catch (JsonException) { }
        return res;
    }

    public void SetZones(IReadOnlyDictionary<string, Ocr.Box> zones) =>
        ZonesJson = JsonSerializer.Serialize(zones.ToDictionary(k => k.Key, k => new[] { Math.Round(k.Value.X, 4), Math.Round(k.Value.Y, 4), Math.Round(k.Value.W, 4), Math.Round(k.Value.H, 4) }));

    public IEnumerable<string> AnchorList => Anchors.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>Contract header terms read from the signed contract (one row per contract number).</summary>
public sealed class ContractTerms : Entity
{
    public string ContractNo { get; set; } = "";
    public DateTime? ContractDate { get; set; }
    public DateTime? SignedDate { get; set; }
    public string FirstParty { get; set; } = "";
    public string FirstPartyCr { get; set; } = "";
    public string Subcontractor { get; set; } = "";
    public string SubcontractorCr { get; set; } = "";
    public string SubcontractorContact { get; set; } = "";
    public string Project { get; set; } = "";
    public string Scope { get; set; } = "";
    public bool LabourOnly { get; set; }
    /// <summary>EXCLUDED / INCLUDED / "" (not stated).</summary>
    public string VatTreatment { get; set; } = "";
    public double VatPct { get; set; }
    /// <summary>Stage payments as text, e.g. "1ST FIX 90%; 2ND FIX 90%; 3RD FIX 90%; HANDOVER 10%".</summary>
    public string PaymentTerms { get; set; } = "";
    public string TrayPaymentTerms { get; set; } = "";
    public double? RetentionPct { get; set; }
    public double? AdvancePct { get; set; }
    public double? DelayPenaltyPerWeek { get; set; }
    public double? DelayPenaltyCapPct { get; set; }
    public int? WarrantyMonths { get; set; }
    public string Termination { get; set; } = "";
    public DateTime? HandoverDate { get; set; }
    public DateTime? CompletionDate { get; set; }
    public long SourceDocId { get; set; }
    public string SourceFile { get; set; } = "";
    public string Status { get; set; } = "DRAFT";
}

/// <summary>One clause (بند) of a contract: number, title, Arabic text as read, page.</summary>
public sealed class ContractClause : Entity
{
    public string ContractNo { get; set; } = "";
    public string ClauseNo { get; set; } = "";
    public string Title { get; set; } = "";
    public string TextAr { get; set; } = "";
    public string TextEn { get; set; } = "";
    public int Page { get; set; }
    public long SourceDocId { get; set; }
    public double Confidence { get; set; }
    /// <summary>Short list of the rules found in this clause (for display; the rules themselves are <see cref="ContractRule"/> rows).</summary>
    public string RulesFound { get; set; } = "";
}
