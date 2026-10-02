using System.Globalization;
using System.Text.RegularExpressions;
using Raffaello.Core.Data;

namespace Raffaello.Core.Variations;

public interface IVariationStore
{
    string User { get; set; }
    Func<DateTime> Clock { get; set; }
    void EnsureSchema();
    List<Variation> Variations();
    Variation? Get(long id);
    List<VariationLine> Lines(long variationId);
    List<VariationLine> AllLines();
    List<VariationDoc> Docs(long variationId);
    List<VariationStatusChange> StatusLog(long variationId);
    string NextNumber(string type);
    Variation Create(Variation v);
    /// <summary>Saves the header and replaces the lines (one transaction). Closed variations cannot be edited.</summary>
    void Save(Variation v, IReadOnlyList<VariationLine> lines);
    Variation SetStatus(Variation v, string to, string note = "", DateTime? at = null);
    VariationDoc AddDoc(VariationDoc d);
    void RemoveDoc(VariationDoc d);
    void Delete(Variation v);
}

public sealed class SqliteVariationStore : SideStore, IVariationStore
{
    public SqliteVariationStore(string path, string user, string? machine = null) : base(path, user, machine) { }

    protected override IEnumerable<Type> Tables => new[] { typeof(Variation), typeof(VariationLine), typeof(VariationDoc), typeof(VariationStatusChange) };

    protected override IEnumerable<string> Indexes => new[]
    {
        "CREATE INDEX IF NOT EXISTS IX_VariationLines_Var ON VariationLines(VariationId);",
        "CREATE INDEX IF NOT EXISTS IX_VariationDocs_Var ON VariationDocs(VariationId);",
    };

    public List<Variation> Variations() => All<Variation>("1=1 ORDER BY Date DESC, Id DESC");
    public Variation? Get(long id) => Get<Variation>(id);
    public List<VariationLine> Lines(long variationId) => All<VariationLine>("VariationId=@V ORDER BY [Order], Id", new { V = variationId });
    public List<VariationLine> AllLines() => All<VariationLine>();
    public List<VariationDoc> Docs(long variationId) => All<VariationDoc>("VariationId=@V ORDER BY AddedAt", new { V = variationId });
    public List<VariationStatusChange> StatusLog(long variationId) => All<VariationStatusChange>("VariationId=@V ORDER BY Id", new { V = variationId });

    /// <summary>VO-001, EI-002 ... (next free number per type).</summary>
    public string NextNumber(string type)
    {
        var t = (type ?? VariationTypes.Vo).Trim().ToUpperInvariant();
        var max = All<Variation>("Type=@T", new { T = t })
            .Select(v => Regex.Match(v.Number, @"(\d+)\s*$")).Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max();
        return $"{t}-{max + 1:000}";
    }

    public Variation Create(Variation v)
    {
        if (string.IsNullOrWhiteSpace(v.Number)) v.Number = NextNumber(v.Type);
        if (All<Variation>("UPPER(Number)=@N", new { N = v.Number.Trim().ToUpperInvariant() }).Count > 0)
            throw new InvalidOperationException($"{v.Number} already exists.");
        if (v.Date == default) v.Date = Clock().Date;
        v.StatusChangedAt = Clock();
        v.CreatedBy = User;
        Batch(b =>
        {
            b.Insert(v);
            b.Insert(new VariationStatusChange { VariationId = v.Id, FromStatus = "", ToStatus = v.Status, At = Clock(), By = User, Note = "created" });
        }, $"Variation {v.Number} created: {v.Title}");
        return v;
    }

    public void Save(Variation v, IReadOnlyList<VariationLine> lines)
    {
        var current = Get(v.Id) ?? throw new InvalidOperationException($"Variation #{v.Id} no longer exists.");
        if (VariationStatus.IsClosed(current.Status))
            throw new InvalidOperationException($"{current.Number} is {current.Status} - it is locked. Move it back to DRAFT (if allowed) to change it.");
        var t = VariationMath.Totals(lines);
        Batch(b =>
        {
            b.Update(v);
            var keep = lines.Where(l => l.Id > 0).Select(l => l.Id).ToHashSet();
            foreach (var old in b.All<VariationLine>("VariationId=@V", new { V = v.Id }).Where(o => !keep.Contains(o.Id))) b.Delete(old);
            var order = 0;
            foreach (var l in lines)
            {
                l.VariationId = v.Id;
                l.Order = ++order;
                if (l.Kind == VariationLineKinds.NewItem) l.Rate = VariationMath.RateOf(l);
                if (l.Id > 0) b.Update(l); else b.Insert(l);
            }
        }, $"Variation {v.Number} saved: {lines.Count} lines, net {t.Net:N2}");
    }

    public Variation SetStatus(Variation v, string to, string note = "", DateTime? at = null)
    {
        var current = Get(v.Id) ?? throw new InvalidOperationException($"Variation #{v.Id} no longer exists.");
        if (current.RowVersion != v.RowVersion) throw new ConcurrencyException("Variations", v.Id, current.UpdatedBy);
        if (!VariationStatus.CanMove(current.Status, to))
            throw new InvalidOperationException($"{current.Number}: {current.Status} -> {to} is not allowed (allowed: {string.Join(", ", VariationStatus.Next(current.Status))}).");
        if (to == VariationStatus.Submitted && Lines(v.Id).Count == 0)
            throw new InvalidOperationException($"{current.Number} has no lines - add the omission / addition / new items before submitting.");
        var when = at ?? Clock();
        var from = current.Status;
        current.Status = to;
        current.StatusChangedAt = when;
        if (to == VariationStatus.Submitted && current.SubmittedAt is null) current.SubmittedAt = when;
        if (VariationStatus.IsClosed(to)) current.DecidedAt = when;
        if (to == VariationStatus.Draft) current.DecidedAt = null;
        Batch(b =>
        {
            b.Update(current);
            b.Insert(new VariationStatusChange { VariationId = v.Id, FromStatus = from, ToStatus = to, At = when, By = User, Note = note });
        }, $"Variation {current.Number}: {from} -> {to}" + (note.Length > 0 ? $" ({note})" : ""));
        return current;
    }

    public VariationDoc AddDoc(VariationDoc d)
    {
        if (d.AddedAt == default) d.AddedAt = Clock();
        return Insert(d, $"Document {d.FileName} added to variation #{d.VariationId}");
    }

    public void RemoveDoc(VariationDoc d) => Delete(d, $"Document {d.FileName} removed from variation #{d.VariationId}");

    public void Delete(Variation v)
    {
        if (v.Status != VariationStatus.Draft) throw new InvalidOperationException("Only DRAFT variations can be deleted - withdraw it instead.");
        Batch(b =>
        {
            foreach (var l in b.All<VariationLine>("VariationId=@V", new { V = v.Id })) b.Delete(l);
            foreach (var d in b.All<VariationDoc>("VariationId=@V", new { V = v.Id })) b.Delete(d);
            b.Delete(v);
        }, $"Variation {v.Number} deleted");
    }
}
