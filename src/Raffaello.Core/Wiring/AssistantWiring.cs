using System.Globalization;
using Raffaello.Core.Assistant;
using Raffaello.Core.Contracts.Rules;
using Raffaello.Core.Data;
using Raffaello.Core.Documents;
using Raffaello.Core.Insights;

namespace Raffaello.Core.Wiring;

// =====================================================================================================
//  Cross-module wiring: small adapters that let one module read another without the modules knowing
//  each other. Each one is tolerant: when the other module cannot be read, the caller keeps working.
// =====================================================================================================

/// <summary>Insights anomalies as assistant items: severity, plain-English explanation, suggested action and evidence citations.</summary>
public static class InsightsAnomalies
{
    /// <summary>Open (not dismissed) insights, most severe first, converted for the assistant's list_anomalies tool.</summary>
    public static IEnumerable<AnomalyItem> ToItems(IEnumerable<Anomaly> anomalies) =>
        anomalies.Where(a => !a.IsDismissed).OrderByDescending(a => a.Severity).ThenByDescending(a => a.Score).Select(ToItem);

    public static AnomalyItem ToItem(Anomaly a)
    {
        var evidence = a.Evidence.Select(Cite).ToList();
        Citation? main = a.Room.Length > 0 ? Citation.Room(a.Room.Split(" / ")[0].Trim())
            : a.InvoiceNo > 0 && a.Subcontractor.Length > 0 ? evidence.FirstOrDefault(e => e.Kind == "invoice") ?? evidence.FirstOrDefault()
            : evidence.FirstOrDefault();
        return new AnomalyItem(a.Tag, a.Kind, a.Title, a.Explanation.Length > 0 ? a.Explanation : a.EvidenceText, main)
        {
            Explanation = a.Explanation, SuggestedAction = a.SuggestedAction, Evidence = evidence, Subcontractor = a.Subcontractor, InvoiceNo = a.InvoiceNo,
        };
    }

    /// <summary>One evidence pointer as a clickable citation (ledger line, invoice, WIR, document with its path ...).</summary>
    public static Citation Cite(Evidence e)
    {
        var id = e.Id.ToString(CultureInfo.InvariantCulture);
        return e.Kind switch
        {
            EvidenceKinds.Ledger => new Citation("ledger", id, e.Label, "Ledger", e.NavKey),
            EvidenceKinds.Room => Citation.Room(e.NavKey.Length > 0 ? e.NavKey : e.Label),
            EvidenceKinds.Invoice => new Citation("invoice", e.NavKey.Length > 0 ? e.NavKey : id, e.Label, "Invoices", e.NavKey),
            EvidenceKinds.InvoiceLine => new Citation("invoiceline", id, e.Label, "Invoices", e.NavKey),
            EvidenceKinds.Statement => new Citation("statement", e.NavKey.Length > 0 ? e.NavKey : id, "INVOICE " + e.Label, "Statements", e.NavKey),
            EvidenceKinds.Wir => Citation.Wir(e.NavKey.Length > 0 ? e.NavKey : e.Label),
            EvidenceKinds.ContractItem => new Citation("contractitem", id, e.Label, "Contracts", e.NavKey),
            EvidenceKinds.Dn => new Citation("dnline", id, e.Label, "Materials", e.NavKey),
            EvidenceKinds.Po => new Citation("po", e.NavKey.Length > 0 ? e.NavKey : id, e.Label, "Materials", e.NavKey),
            _ => Citation.Document("ev" + id, e.Label, e.Path),
        };
    }
}

/// <summary>
/// The Documents module's full-text archive (local SQLite FTS5 trigram index, or PostgreSQL full text on the server) as the assistant's
/// document search: every hit is a page of a read document, with the record it fed (contract, DN, PO, statement ...).
/// </summary>
public sealed class ArchiveDocumentSearch : IDocumentSearch
{
    private readonly Func<IDocumentStore?> _store;
    public ArchiveDocumentSearch(Func<IDocumentStore?> store) => _store = store;
    public ArchiveDocumentSearch(IDocumentStore store) : this(() => store) { }

    public string Name => "document archive (full text)";

    public IReadOnlyList<DocumentHit> Search(string query, int limit)
    {
        var store = _store() ?? throw new InvalidOperationException("The document archive is not available.");
        var hits = store.Search(query, Math.Clamp(limit, 1, 200));
        if (hits.Count == 0) return Array.Empty<DocumentHit>();
        var recs = store.All<DocRecord>().ToDictionary(d => d.Id);
        return hits.Select(h =>
        {
            recs.TryGetValue(h.DocRecordId, out var r);
            var title = (h.FileName.Length > 0 ? h.FileName : $"document #{h.DocRecordId}") + (h.DocType.Length > 0 ? $" ({h.DocType})" : "");
            return new DocumentHit("docrec" + h.DocRecordId.ToString(CultureInfo.InvariantCulture), title, r?.StoredPath ?? "", h.Page, h.Snippet, "doc")
            {
                LinkedTable = h.LinkedTable, LinkedKey = h.LinkedKey, DocType = h.DocType,
            };
        }).ToList();
    }

    /// <summary>The record a read document fed, as a citation (null when the document is not linked or the table is unknown).</summary>
    public static Citation? LinkedCitation(string table, string key)
    {
        if (string.IsNullOrWhiteSpace(table) || string.IsNullOrWhiteSpace(key)) return null;
        var t = table.Trim().ToUpperInvariant();
        if (t.Contains("CONTRACT")) return Citation.Contract(key);
        if (t.Contains("DN") && !t.Contains("LINE")) return Citation.Dn("", key);
        if (t.Contains("PO")) return Citation.Po(key);
        if (t.Contains("VARIATION")) return Citation.Variation(key);
        if (t.Contains("WIR")) return Citation.Wir(key);
        if (t.Contains("SUBINVOICE") || t == "INVOICE" || t == "INVOICES") return new Citation("invoice", key, "INVOICE " + key, "Invoices", key);
        if (t.Contains("STATEMENT")) return new Citation("statement", key, "STATEMENT " + key, "SiteStatements", key);
        if (t.Contains("MIR")) return new Citation("mir", key, "MIR " + key, "Materials", key);
        return new Citation("record", $"{table}|{key}", $"{table} {key}");
    }
}

/// <summary>The obligations calendar (handover, warranty end, retention release, penalty start / cap) from the signed contracts.</summary>
public static class ContractObligations
{
    public static List<Obligation> Build(IDocumentStore docs, ProjectSnapshot? project) =>
        Build(docs.All<ContractTerms>(), docs.All<ContractRule>(), project);

    /// <summary>Same, read straight from a project store (the server: the contract tables are module tables of the same database).</summary>
    public static List<Obligation> Build(IProjectStore store, ProjectSnapshot? project)
    {
        try { return Build(store.All<ContractTerms>(), store.All<ContractRule>(), project); }
        catch (Exception) { return new(); }
    }

    public static List<Obligation> Build(IEnumerable<ContractTerms> terms, IEnumerable<ContractRule> rules, ProjectSnapshot? project)
    {
        var t = terms.ToList();
        if (t.Count == 0) return new();
        var values = project?.ContractItems.GroupBy(i => i.ContractNo).ToDictionary(g => g.Key, g => g.Sum(i => i.Qty * i.Rate)) ?? new Dictionary<string, double>();
        if (project != null)
            foreach (var c in project.Contracts.Where(c => c.Value > 0 && !values.ContainsKey(c.ContractNo))) values[c.ContractNo] = c.Value;
        return ObligationsCalendar.Build(t, rules, values);
    }

    /// <summary>"Needs you today" items for obligations due within 30 days (overdue first).</summary>
    public static IEnumerable<Queue.QueueItem> Queue(IReadOnlyList<Obligation> obligations, DateTime today) => ObligationsCalendar.Queue(obligations, today);
}
