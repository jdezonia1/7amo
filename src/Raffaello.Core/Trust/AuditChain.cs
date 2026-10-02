using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Raffaello.Core.Trust;

/// <summary>
/// One audit row as the hash chain sees it. The stores keep their own AuditLog shape; this is the subset that is sealed
/// (every column a person could change to rewrite history) plus the chain columns.
/// </summary>
public sealed class AuditChainRow
{
    public long Id { get; set; }
    public long? ChainSeq { get; set; }
    public DateTime At { get; set; }
    public string? User { get; set; }
    public string? Machine { get; set; }
    public string? TableName { get; set; }
    public long RowId { get; set; }
    public string? Action { get; set; }
    public string? Summary { get; set; }
    public string? Changes { get; set; }
    /// <summary>Server only (PostgreSQL keeps the full before / after row).</summary>
    public string? OldJson { get; set; }
    public string? NewJson { get; set; }
    public string? PrevHash { get; set; }
    public string? Hash { get; set; }
}

/// <summary>
/// The chain rule (identical on the desktop SQLite file and on the server):
/// <c>Hash(n) = SHA-256( Hash(n-1) + "\n" + canonical(row n) )</c>, hex lower-case, with <see cref="Genesis"/> before the first row.
/// The canonical text length-prefixes every field (no separator can be forged), keeps the timestamp to the millisecond
/// (both databases store at least that) and marks NULL differently from an empty string.
/// </summary>
public static class AuditHash
{
    public const string Genesis = "GENESIS";
    public const string Version = "RAFFAELLO-AUDIT-1";

    public static string Content(AuditChainRow r, long seq)
    {
        var sb = new StringBuilder(256);
        sb.Append(Version).Append('|');
        Field(sb, seq.ToString(CultureInfo.InvariantCulture));
        Field(sb, r.Id.ToString(CultureInfo.InvariantCulture));
        Field(sb, r.At.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture));
        Field(sb, r.User);
        Field(sb, r.Machine);
        Field(sb, r.TableName);
        Field(sb, r.RowId.ToString(CultureInfo.InvariantCulture));
        Field(sb, r.Action);
        Field(sb, r.Summary);
        Field(sb, r.Changes);
        Field(sb, r.OldJson);
        Field(sb, r.NewJson);
        return sb.ToString();
    }

    private static void Field(StringBuilder sb, string? v)
    {
        if (v is null) { sb.Append("-1:|"); return; }
        sb.Append(v.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(v).Append('|');
    }

    public static string Compute(string prevHash, AuditChainRow r, long seq) => Sha256Hex(prevHash + "\n" + Content(r, seq));

    public static string Sha256Hex(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public static string Short(string? hash) => string.IsNullOrEmpty(hash) ? "-" : hash.Length > 12 ? hash[..12] : hash;
}

/// <summary>
/// The sealed end of the chain, kept by the store in the same transaction as the rows it seals. <see cref="BaseSeq"/> /
/// <see cref="BaseHash"/> are where the chain starts (0 / GENESIS, or the head at the moment of a sanctioned data reset).
/// </summary>
public sealed class AuditChainHead
{
    public long BaseSeq { get; set; }
    public string BaseHash { get; set; } = AuditHash.Genesis;
    public long LastSeq { get; set; }
    public string LastHash { get; set; } = AuditHash.Genesis;
    public long LastId { get; set; }
    public DateTime? ResetAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>A copy of the chain head kept somewhere else (the user's PC, the server's backup folder). History before an anchor cannot be rewritten without the anchor showing it.</summary>
public sealed class AuditAnchor
{
    public long Seq { get; set; }
    public string Hash { get; set; } = "";
    public DateTime At { get; set; }
    public string Source { get; set; } = "";
}

public static class AuditProblemKinds
{
    /// <summary>The row's content no longer matches its seal.</summary>
    public const string Edited = "EDITED";
    /// <summary>Sequence numbers are missing (rows deleted).</summary>
    public const string Deleted = "DELETED";
    /// <summary>The row does not point at the hash of the row before it (re-sealed or moved).</summary>
    public const string LinkBroken = "LINK BROKEN";
    /// <summary>The newest rows are gone (the chain ends before the recorded head / an anchor).</summary>
    public const string TailDeleted = "TAIL DELETED";
    public const string HeadMismatch = "HEAD MISMATCH";
    public const string AnchorMismatch = "ANCHOR MISMATCH";
    public const string Duplicate = "DUPLICATE SEQUENCE";
    /// <summary>Rows written but not sealed for a long time (warning only).</summary>
    public const string Unsealed = "NOT SEALED";
}

public sealed class AuditProblem
{
    public string Kind { get; set; } = "";
    public long? Seq { get; set; }
    public long? RowId { get; set; }
    public string Message { get; set; } = "";
    /// <summary>True for warnings that do not mean tampering (e.g. rows still waiting to be sealed).</summary>
    public bool IsWarning { get; set; }
    public override string ToString() => $"{Kind}{(Seq is { } s ? $" at #{s}" : "")}: {Message}";
}

public sealed class AuditVerifyReport
{
    public string Source { get; set; } = "";
    public DateTime VerifiedAt { get; set; } = DateTime.Now;
    public long RowsChecked { get; set; }
    public long Unsealed { get; set; }
    public long FirstSeq { get; set; }
    public long LastSeq { get; set; }
    public string LastHash { get; set; } = "";
    public long BaseSeq { get; set; }
    public DateTime? ResetAt { get; set; }
    public int AnchorsChecked { get; set; }
    public List<AuditProblem> Problems { get; set; } = new();

    public bool Ok => Problems.All(p => p.IsWarning);
    public int Errors => Problems.Count(p => !p.IsWarning);

    public string Summary => Ok
        ? $"AUDIT LOG INTACT - {RowsChecked:N0} sealed rows (#{FirstSeq}..#{LastSeq}), head {AuditHash.Short(LastHash)}" +
          (AnchorsChecked > 0 ? $", {AnchorsChecked} anchor(s) match" : "") + (Unsealed > 0 ? $", {Unsealed} row(s) waiting to be sealed" : "") +
          (ResetAt is { } r ? $"; chain continues after the data reset of {r:dd-MMM-yyyy HH:mm}" : "")
        : $"AUDIT LOG TAMPERED - {Errors} problem(s) in {RowsChecked:N0} rows: " + string.Join("; ", Problems.Where(p => !p.IsWarning).Take(5).Select(p => p.ToString()));

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Raffaello audit integrity check - {Source}");
        sb.AppendLine($"Verified at {VerifiedAt:dd-MMM-yyyy HH:mm:ss}");
        sb.AppendLine(Summary);
        foreach (var p in Problems) sb.AppendLine((p.IsWarning ? "  warning  " : "  PROBLEM  ") + p);
        return sb.ToString();
    }
}

/// <summary>
/// Walks a chain (rows ordered by sequence) and reports every edited, deleted, re-linked or truncated row. Streaming: the rows
/// are not kept in memory, so a server with millions of audit rows can be verified.
/// </summary>
public static class AuditChainVerifier
{
    public static AuditVerifyReport Verify(IEnumerable<AuditChainRow> rowsBySeq, AuditChainHead? head, long unsealed = 0, DateTime? oldestUnsealedAt = null,
        IEnumerable<AuditAnchor>? anchors = null, string source = "", DateTime? now = null)
    {
        var rep = new AuditVerifyReport { Source = source, Unsealed = unsealed, BaseSeq = head?.BaseSeq ?? 0, ResetAt = head?.ResetAt, VerifiedAt = now ?? DateTime.Now };
        var expectedSeq = (head?.BaseSeq ?? 0) + 1;
        var prevHash = head?.BaseHash ?? AuditHash.Genesis;
        var anchorList = (anchors ?? Array.Empty<AuditAnchor>()).Where(a => a.Seq > 0 && a.Hash.Length > 0).ToList();
        var anchorSeqs = anchorList.Select(a => a.Seq).ToHashSet();
        var seenAnchors = new Dictionary<long, string>();
        long last = 0;
        string lastHash = "";
        var first = true;
        var editedRun = 0;

        foreach (var r in rowsBySeq)
        {
            var seq = r.ChainSeq ?? 0;
            if (first) { rep.FirstSeq = seq; first = false; }
            rep.RowsChecked++;
            if (seq <= last && last > 0)
            {
                rep.Problems.Add(new AuditProblem { Kind = AuditProblemKinds.Duplicate, Seq = seq, RowId = r.Id, Message = $"sequence #{seq} appears again (row id {r.Id})." });
                continue;
            }
            var gap = seq > expectedSeq;
            if (gap)
                rep.Problems.Add(new AuditProblem
                {
                    Kind = AuditProblemKinds.Deleted, Seq = expectedSeq, RowId = r.Id,
                    Message = seq - expectedSeq == 1 ? $"row #{expectedSeq} is missing (deleted)." : $"rows #{expectedSeq}..#{seq - 1} are missing ({seq - expectedSeq} rows deleted).",
                });
            else if (seq < expectedSeq)
                rep.Problems.Add(new AuditProblem { Kind = AuditProblemKinds.Duplicate, Seq = seq, RowId = r.Id, Message = $"sequence #{seq} is before the start of the chain (#{expectedSeq})." });

            if (!gap && !string.Equals(r.PrevHash, prevHash, StringComparison.Ordinal))
                rep.Problems.Add(new AuditProblem
                {
                    Kind = AuditProblemKinds.LinkBroken, Seq = seq, RowId = r.Id,
                    Message = $"row #{seq} does not follow row #{seq - 1} (points at {AuditHash.Short(r.PrevHash)}, expected {AuditHash.Short(prevHash)}).",
                });

            var recomputed = AuditHash.Compute(r.PrevHash ?? "", r, seq);
            if (!string.Equals(recomputed, r.Hash, StringComparison.Ordinal))
            {
                editedRun++;
                if (editedRun <= 200)
                    rep.Problems.Add(new AuditProblem
                    {
                        Kind = AuditProblemKinds.Edited, Seq = seq, RowId = r.Id,
                        Message = $"row #{seq} ({r.At:dd-MMM-yyyy HH:mm} {r.User} {r.Action} {r.TableName} #{r.RowId}) was changed after it was sealed.",
                    });
            }
            if (anchorSeqs.Contains(seq)) seenAnchors[seq] = r.Hash ?? "";
            prevHash = r.Hash ?? "";
            last = seq;
            lastHash = r.Hash ?? "";
            expectedSeq = seq + 1;
        }
        rep.LastSeq = last;
        rep.LastHash = lastHash;
        if (rep.RowsChecked == 0) { rep.FirstSeq = 0; rep.LastHash = head?.BaseHash ?? AuditHash.Genesis; }

        if (head != null)
        {
            if (last < head.LastSeq)
                rep.Problems.Add(new AuditProblem
                {
                    Kind = AuditProblemKinds.TailDeleted, Seq = last + 1,
                    Message = $"the chain ends at #{last} but #{head.LastSeq} was sealed - the newest {head.LastSeq - last} row(s) were deleted.",
                });
            else if (last == head.LastSeq && last > head.BaseSeq && !string.Equals(lastHash, head.LastHash, StringComparison.Ordinal))
                rep.Problems.Add(new AuditProblem { Kind = AuditProblemKinds.HeadMismatch, Seq = last, Message = $"the last row's seal {AuditHash.Short(lastHash)} differs from the recorded head {AuditHash.Short(head.LastHash)}." });
            else if (last > head.LastSeq)
                rep.Problems.Add(new AuditProblem { Kind = AuditProblemKinds.HeadMismatch, Seq = head.LastSeq + 1, Message = $"rows #{head.LastSeq + 1}..#{last} were sealed outside Raffaello (head says #{head.LastSeq})." });
        }

        foreach (var a in anchorList.OrderBy(a => a.Seq))
        {
            if (a.Seq <= rep.BaseSeq) continue;   // before a sanctioned reset
            rep.AnchorsChecked++;
            if (!seenAnchors.TryGetValue(a.Seq, out var h))
                rep.Problems.Add(new AuditProblem
                {
                    Kind = a.Seq > last ? AuditProblemKinds.TailDeleted : AuditProblemKinds.Deleted, Seq = a.Seq,
                    Message = $"row #{a.Seq} was seen on {a.At:dd-MMM-yyyy HH:mm} ({a.Source}) but is no longer in the chain.",
                });
            else if (!string.Equals(h, a.Hash, StringComparison.Ordinal))
                rep.Problems.Add(new AuditProblem
                {
                    Kind = AuditProblemKinds.AnchorMismatch, Seq = a.Seq,
                    Message = $"row #{a.Seq} had seal {AuditHash.Short(a.Hash)} on {a.At:dd-MMM-yyyy HH:mm} ({a.Source}); now {AuditHash.Short(h)} - history was rewritten.",
                });
        }

        if (unsealed > 0 && oldestUnsealedAt is { } old && rep.VerifiedAt - old > TimeSpan.FromMinutes(10))
            rep.Problems.Add(new AuditProblem
            {
                Kind = AuditProblemKinds.Unsealed, IsWarning = true,
                Message = $"{unsealed} row(s) written since {old:dd-MMM HH:mm} are not sealed yet (the server's sealer may be stopped).",
            });
        return rep;
    }
}

/// <summary>
/// Chain heads remembered on this PC (one list per data location). After every successful verification the head is saved;
/// the next verification checks those rows still carry the same seal - so a complete rewrite of the chain (new hashes for
/// everything) is still detected by anyone who verified before.
/// </summary>
public sealed class AuditAnchorStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string Path { get; }
    public int KeepPerLocation { get; set; } = 60;

    public AuditAnchorStore(string? path = null) =>
        Path = path ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Raffaello", "audit-anchors.json");

    private Dictionary<string, List<AuditAnchor>> Load()
    {
        try
        {
            return File.Exists(Path)
                ? JsonSerializer.Deserialize<Dictionary<string, List<AuditAnchor>>>(File.ReadAllText(Path)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public List<AuditAnchor> For(string location) =>
        Load().TryGetValue(Key(location), out var l) ? l : new List<AuditAnchor>();

    /// <summary>Remembers the head of a chain that verified OK.</summary>
    public void Remember(string location, AuditVerifyReport report)
    {
        if (!report.Ok || report.LastSeq <= 0 || report.LastHash.Length == 0) return;
        var all = Load();
        var key = Key(location);
        if (!all.TryGetValue(key, out var list)) all[key] = list = new List<AuditAnchor>();
        if (list.Any(a => a.Seq == report.LastSeq)) return;
        list.Add(new AuditAnchor { Seq = report.LastSeq, Hash = report.LastHash, At = report.VerifiedAt, Source = Environment.MachineName + "\\" + Environment.UserName });
        // keep the oldest few and the newest ones: old anchors protect old history
        if (list.Count > KeepPerLocation)
        {
            var keep = list.OrderBy(a => a.Seq).Take(10).Concat(list.OrderByDescending(a => a.Seq).Take(KeepPerLocation - 10)).Distinct().OrderBy(a => a.Seq).ToList();
            list.Clear(); list.AddRange(keep);
        }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(all, Json));
    }

    /// <summary>Forgets the anchors of a location (after a sanctioned data reset).</summary>
    public void Forget(string location)
    {
        var all = Load();
        if (all.Remove(Key(location)))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(all, Json));
        }
    }

    private static string Key(string location) => (location ?? "").Trim().TrimEnd('/', '\\').ToUpperInvariant();
}
