using Microsoft.Data.Sqlite;
using Raffaello.Core.Contracts.Rules;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Documents;

/// <summary>A page found by the archive search.</summary>
public sealed record DocSearchHit(long DocRecordId, string FileName, string DocType, string LinkedTable, string LinkedKey, int Page, string Snippet, double Rank);

/// <summary>
/// Persistence for read documents (evidence index, page text, extracted fields, learned templates) and contract intelligence
/// (terms, clauses, rules, bypasses). SQLite in the shared data file (with an FTS5 index) or the server (PostgreSQL full text).
/// </summary>
public interface IDocumentStore
{
    string User { get; }
    void EnsureSchema();
    List<T> All<T>() where T : Entity, new();
    T Insert<T>(T entity, string? summary = null) where T : Entity;
    T Update<T>(T entity, string? summary = null) where T : Entity, new();
    void Delete<T>(T entity, string? summary = null) where T : Entity;
    void Batch(Action<IStoreBatch> work, string summary);
    /// <summary>Full-text search over every read page (numbers, codes, Arabic words).</summary>
    List<DocSearchHit> Search(string query, int take = 50);
}

public static class DocumentEntities
{
    public static readonly Type[] All =
    {
        typeof(DocRecord), typeof(DocPageText), typeof(DocField), typeof(ReadTemplate), typeof(ContractTerms), typeof(ContractClause), typeof(ContractRule), typeof(RuleBypass),
    };
}

/// <summary>Operations shared by the SQLite and server stores (written once against the generic entity primitives).</summary>
public static class DocumentStoreExtensions
{
    /// <summary>Saves a read document with its page text and fields. A document with the same SHA-256 and link is replaced (re-read).</summary>
    public static DocRecord SaveRead(this IDocumentStore store, DocRecord rec, IReadOnlyList<DocPageText> pages, IReadOnlyList<DocField> fields)
    {
        var old = store.All<DocRecord>().FirstOrDefault(d => d.LinkedTable == rec.LinkedTable && d.LinkedKey == rec.LinkedKey
            && (rec.Sha256.Length > 0 ? d.Sha256 == rec.Sha256 : d.Sha256.Length == 0 && d.FileName == rec.FileName));
        var oldPages = old is null ? new List<DocPageText>() : store.All<DocPageText>().Where(p => p.DocRecordId == old.Id).ToList();
        var oldFields = old is null ? new List<DocField>() : store.All<DocField>().Where(f => f.DocRecordId == old.Id).ToList();
        foreach (var p in pages) if (p.NormText.Length == 0) p.NormText = ArabicText.Normalize(p.Text);
        store.Batch(w =>
        {
            if (old != null)
            {
                rec.Id = old.Id; rec.RowVersion = old.RowVersion;
                w.Update(rec);
                foreach (var p in oldPages) w.Delete(p);
                foreach (var f in oldFields) w.Delete(f);
            }
            else w.Insert(rec);
            foreach (var p in pages) { p.Id = 0; p.DocRecordId = rec.Id; }
            foreach (var f in fields) { f.Id = 0; f.DocRecordId = rec.Id; }
            w.InsertMany(pages);
            w.InsertMany(fields);
        }, $"Document read: {rec.FileName} ({rec.DocType}, {pages.Count} page(s), {fields.Count} field(s)){(rec.LinkedKey.Length > 0 ? " -> " + rec.LinkedTable + " " + rec.LinkedKey : "")}");
        return rec;
    }

    /// <summary>Replaces the terms, clauses and READ rules of a contract (user-edited rules and bypasses are kept).</summary>
    public static void SaveContractIntelligence(this IDocumentStore store, ContractTerms terms, IReadOnlyList<ContractClause> clauses, IReadOnlyList<ContractRule> rules)
    {
        var no = terms.ContractNo;
        var oldTerms = store.All<ContractTerms>().Where(t => t.ContractNo == no).ToList();
        var oldClauses = store.All<ContractClause>().Where(c => c.ContractNo == no).ToList();
        var oldRules = store.All<ContractRule>().Where(r => r.ContractNo == no && r.Origin == "READ").ToList();
        var userTypes = store.All<ContractRule>().Where(r => r.ContractNo == no && r.Origin == "USER").Select(r => r.RuleType + "|" + r.ParamsJson).ToHashSet();
        store.Batch(w =>
        {
            var keep = oldTerms.FirstOrDefault();
            if (keep != null)
            {
                terms.Id = keep.Id; terms.RowVersion = keep.RowVersion;
                // dates the user entered on the record survive a re-read
                terms.HandoverDate ??= keep.HandoverDate;
                terms.CompletionDate ??= keep.CompletionDate;
                w.Update(terms);
                foreach (var t in oldTerms.Skip(1)) w.Delete(t);
            }
            else w.Insert(terms);
            foreach (var c in oldClauses) w.Delete(c);
            foreach (var r in oldRules) w.Delete(r);
            foreach (var c in clauses) { c.Id = 0; c.ContractNo = no; c.SourceDocId = terms.SourceDocId; }
            w.InsertMany(clauses);
            var fresh = rules.Where(r => !userTypes.Contains(r.RuleType + "|" + r.ParamsJson)).ToList();
            foreach (var r in fresh) { r.Id = 0; r.ContractNo = no; }
            w.InsertMany(fresh);
        }, $"Contract {no}: terms, {clauses.Count} clauses, {rules.Count} rules read from the signed contract");
    }

    public static RuleBypass RecordBypass(this IDocumentStore store, RuleWarning w, string reason, DateTime now)
    {
        var b = ContractRuleEngine.Bypass(w, reason, store.User, now);
        return store.Insert(b, $"BYPASS {w.RuleType} on {w.Context} {w.ContextKey}: {w.Message} - reason: {b.Reason}");
    }

    public static ContractRuleEngine RuleEngine(this IDocumentStore store) =>
        new(store.All<ContractRule>(), store.All<RuleBypass>(), store.All<ContractClause>());

    /// <summary>The learned template for (type, issuer), or the one whose anchors all appear in the text.</summary>
    public static ReadTemplate? FindTemplate(this IDocumentStore store, string docType, string? issuer, string pageText)
    {
        var fold = ArabicText.Normalize(pageText);
        var list = store.All<ReadTemplate>().Where(t => t.DocType == docType).ToList();
        return list.FirstOrDefault(t => issuer != null && string.Equals(t.Issuer, issuer, StringComparison.OrdinalIgnoreCase))
               ?? list.Where(t => t.AnchorList.Any() && t.AnchorList.All(a => fold.Contains(ArabicText.Normalize(a)))).OrderByDescending(t => t.Confirmations).FirstOrDefault();
    }

    /// <summary>In-memory search used by stores without an index (and as the server's fallback): every query word must appear.</summary>
    public static List<DocSearchHit> SearchInMemory(IEnumerable<DocPageText> pages, IEnumerable<DocRecord> docs, string query, int take)
    {
        var words = ArabicText.Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return new();
        var byId = docs.ToDictionary(d => d.Id);
        return pages.Where(p => words.All(w => p.NormText.Contains(w, StringComparison.Ordinal)))
            .Select(p =>
            {
                byId.TryGetValue(p.DocRecordId, out var d);
                return new DocSearchHit(p.DocRecordId, d?.FileName ?? "", d?.DocType ?? p.Kind, d?.LinkedTable ?? "", d?.LinkedKey ?? "", p.Page, Snippet(p.NormText, words[0]), words.Sum(w => Count(p.NormText, w)));
            })
            .OrderByDescending(h => h.Rank).Take(take).ToList();
    }

    private static int Count(string s, string w) { var n = 0; var i = 0; while ((i = s.IndexOf(w, i, StringComparison.Ordinal)) >= 0) { n++; i += w.Length; } return n; }

    public static string Snippet(string text, string word, int around = 60)
    {
        var i = text.IndexOf(word, StringComparison.Ordinal);
        if (i < 0) return text.Length <= around * 2 ? text : text[..(around * 2)] + " ...";
        var a = Math.Max(0, i - around); var b = Math.Min(text.Length, i + word.Length + around);
        return (a > 0 ? "... " : "") + text[a..b].Replace('\n', ' ') + (b < text.Length ? " ..." : "");
    }
}

/// <summary>
/// SQLite document store in the shared data file (same file, pragmas and audit log as <see cref="Db"/>), with an FTS5 trigram index
/// over the normalised page text so substrings of numbers and codes ("81064344", "SUB-ELE-028") and Arabic words are found.
/// </summary>
public sealed class SqliteDocumentStore : IDocumentStore
{
    private readonly Func<Db> _db;
    private string? _schemaPath;

    public SqliteDocumentStore(Func<Db> db) => _db = db;
    public SqliteDocumentStore(Db db) : this(() => db) { }

    private Db Db
    {
        get
        {
            var db = _db();
            if (_schemaPath != db.Path) { EnsureSchema(db); _schemaPath = db.Path; }
            return db;
        }
    }

    public string User => _db().User;
    public void EnsureSchema() { EnsureSchema(_db()); _schemaPath = _db().Path; }

    private static void EnsureSchema(Db db)
    {
        db.EnsureTables(DocumentEntities.All);
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE INDEX IF NOT EXISTS IX_DocPageTexts_Doc ON DocPageTexts(DocRecordId);
            CREATE INDEX IF NOT EXISTS IX_DocFields_Doc ON DocFields(DocRecordId);
            CREATE VIRTUAL TABLE IF NOT EXISTS DocSearch USING fts5(NormText, tokenize = 'trigram');
            """;
        cmd.ExecuteNonQuery();
    }

    public List<T> All<T>() where T : Entity, new() => Db.All<T>();
    public T Insert<T>(T entity, string? summary = null) where T : Entity => Db.Insert(entity, summary);
    public T Update<T>(T entity, string? summary = null) where T : Entity, new() => Db.Update(entity, summary);
    public void Delete<T>(T entity, string? summary = null) where T : Entity => Db.Delete(entity, summary);
    public void Batch(Action<IStoreBatch> work, string summary) => Db.Batch(work, summary);

    /// <summary>Brings the FTS index in line with DocPageTexts (rowid = page-text id): adds new pages, drops deleted ones.</summary>
    public void SyncIndex()
    {
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            DELETE FROM DocSearch WHERE rowid NOT IN (SELECT Id FROM DocPageTexts);
            INSERT INTO DocSearch(rowid, NormText) SELECT Id, NormText FROM DocPageTexts WHERE Id NOT IN (SELECT rowid FROM DocSearch);
            """;
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    public List<DocSearchHit> Search(string query, int take = 50)
    {
        var words = ArabicText.Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return new();
        if (words.Any(w => w.Length < 3))
            return DocumentStoreExtensions.SearchInMemory(Db.All<DocPageText>(), Db.All<DocRecord>(), query, take);
        SyncIndex();
        var match = string.Join(" AND ", words.Select(w => "\"" + w.Replace("\"", "\"\"") + "\""));
        var ids = new List<(long Id, double Rank)>();
        using (var c = Db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT rowid, bm25(DocSearch) FROM DocSearch WHERE DocSearch MATCH @q ORDER BY bm25(DocSearch) LIMIT @n";
            cmd.Parameters.AddWithValue("@q", match);
            cmd.Parameters.AddWithValue("@n", take);
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add((r.GetInt64(0), -r.GetDouble(1)));
        }
        if (ids.Count == 0) return new();
        var pages = Db.All<DocPageText>().Where(p => ids.Any(i => i.Id == p.Id)).ToDictionary(p => p.Id);
        var docs = Db.All<DocRecord>().ToDictionary(d => d.Id);
        return ids.Where(i => pages.ContainsKey(i.Id)).Select(i =>
        {
            var p = pages[i.Id];
            docs.TryGetValue(p.DocRecordId, out var d);
            return new DocSearchHit(p.DocRecordId, d?.FileName ?? "", d?.DocType ?? p.Kind, d?.LinkedTable ?? "", d?.LinkedKey ?? "", p.Page, DocumentStoreExtensions.Snippet(p.NormText, words[0]), i.Rank);
        }).ToList();
    }
}
