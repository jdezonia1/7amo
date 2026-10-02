using System.Text;
using System.Text.Json;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Remote;

public sealed class TableMigration
{
    public string Table { get; init; } = "";
    public int Local { get; set; }
    public int OnServer { get; set; }
    /// <summary>Local rows the server does not have yet (same id): these are copied.</summary>
    public int ToCopy { get; set; }
    /// <summary>Same id on both sides with the same content (already migrated).</summary>
    public int Same { get; set; }
    /// <summary>Same id, different content: the server copy is kept (changed on the server after migration, or other data).</summary>
    public int Different { get; set; }
    public int ServerOnly { get; set; }
    public int Copied { get; set; }
}

public sealed class MigrationReport
{
    public bool DryRun { get; init; }
    public string Source { get; init; } = "";
    public string SourceKey { get; init; } = "";
    public string? ServerSource { get; init; }
    public List<TableMigration> Tables { get; } = new();
    public int AuditRows { get; set; }
    public List<string> Warnings { get; } = new();
    public bool Blocked { get; set; }

    public int ToCopy => Tables.Sum(t => t.ToCopy);
    public int Copied => Tables.Sum(t => t.Copied);

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(DryRun ? "MIGRATION DRY RUN (nothing written)" : "MIGRATION REPORT");
        sb.AppendLine($"From: {Source}");
        sb.AppendLine($"{"TABLE",-22}{"LOCAL",8}{"SERVER",8}{"COPY",8}{"SAME",8}{"DIFF",8}{"SRV ONLY",9}" + (DryRun ? "" : $"{"COPIED",8}"));
        foreach (var t in Tables.Where(t => t.Local + t.OnServer > 0))
            sb.AppendLine($"{t.Table,-22}{t.Local,8}{t.OnServer,8}{t.ToCopy,8}{t.Same,8}{t.Different,8}{t.ServerOnly,9}" + (DryRun ? "" : $"{t.Copied,8}"));
        sb.AppendLine(DryRun ? $"{ToCopy} rows would be copied; ids are kept so all links stay valid. Running it again copies nothing twice."
                             : $"{Copied} rows copied, {AuditRows} audit history rows added.");
        foreach (var w in Warnings) sb.AppendLine("WARNING: " + w);
        if (Blocked) sb.AppendLine("BLOCKED: fix the warnings above (or use 'force' if you are sure).");
        return sb.ToString();
    }
}

/// <summary>
/// Copies a local SQLite data file to the server. Idempotent: rows keep their ids (foreign keys stay valid), rows already on
/// the server are skipped, the audit history is copied once. The dry run reports what would happen without writing.
/// Blocks when the server already holds data from another source (two projects would be mixed) unless forced.
/// </summary>
public static class LocalToServerMigrator
{
    public const string ServerSourceKey = "Migration:Source";
    private const int MaxChunkChars = 8_000_000;

    public static MigrationReport Plan(IProjectStore local, RemoteProjectStore server, bool force = false) => Execute(local, server, dryRun: true, force, null);

    public static MigrationReport Run(IProjectStore local, RemoteProjectStore server, bool force = false, Action<string>? progress = null) =>
        Execute(local, server, dryRun: false, force, progress);

    /// <summary>A stable id for the local data file (stored in its Meta on first use).</summary>
    public static string SourceKeyOf(IProjectStore local)
    {
        var k = local.GetMeta("DataFileId");
        if (string.IsNullOrEmpty(k)) { k = Guid.NewGuid().ToString("N"); local.SetMeta("DataFileId", k); }
        return k;
    }

    private static MigrationReport Execute(IProjectStore local, RemoteProjectStore server, bool dryRun, bool force, Action<string>? progress)
    {
        if (!server.CheckNow()) throw new ServerUnavailableException($"The server {server.Location} is not reachable - the migration needs a connection.");
        local.EnsureSchema();
        ModuleEntities.RegisterAll();   // [phase6] materials / Aconex / variation tables are copied too
        // [assemblies] begin
        Assemblies.AssemblyEntities.Register();
        // [assemblies] end
        var key = SourceKeyOf(local);
        var serverSource = server.Api.GetMeta(ServerSourceKey);
        var report = new MigrationReport { DryRun = dryRun, Source = local.Location, SourceKey = key, ServerSource = serverSource };

        var plans = new List<(Type Type, TableMigration Plan, List<Entity> Copy)>();
        foreach (var t in EntityMeta.All)
        {
            var table = EntityMeta.TableOf(t);
            progress?.Invoke($"Comparing {table}...");
            List<Entity> mine;
            try { mine = EntityMeta.AllOf(local, t); }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException)
            {
                mine = new List<Entity>();   // the local file has no table for this type (added by another module): nothing to copy
            }
            var theirs = EntityMeta.AllOf(server, t).ToDictionary(e => e.Id);
            var plan = new TableMigration { Table = table, Local = mine.Count, OnServer = theirs.Count };
            var copy = new List<Entity>();
            var localIds = new HashSet<long>();
            foreach (var e in mine)
            {
                localIds.Add(e.Id);
                if (!theirs.TryGetValue(e.Id, out var s)) { plan.ToCopy++; copy.Add(e); }
                else if (Comparable(e) == Comparable(s)) plan.Same++;
                else plan.Different++;
            }
            plan.ServerOnly = theirs.Keys.Count(id => !localIds.Contains(id));
            report.Tables.Add(plan);
            plans.Add((t, plan, copy));
        }

        var serverRows = report.Tables.Sum(t => t.OnServer);
        if (serverRows > 0 && serverSource != key)
        {
            report.Warnings.Add(serverSource is null
                ? $"The server already holds {serverRows} rows that did not come from this data file."
                : $"The server data was migrated from another data file ({serverSource[..Math.Min(8, serverSource.Length)]}...).");
            report.Blocked = !force;
        }
        var differ = report.Tables.Sum(t => t.Different);
        if (differ > 0) report.Warnings.Add($"{differ} rows exist on both sides with different content - the server copies are kept.");
        if (dryRun || report.Blocked) return report;

        foreach (var (t, plan, copy) in plans.Where(p => p.Copy.Count > 0))
        {
            foreach (var chunk in Chunks(copy))
            {
                progress?.Invoke($"Copying {plan.Table} ({plan.Copied + chunk.Count}/{copy.Count})...");
                var r = server.Api.MigrateImport(new MigrateImportRequest
                {
                    Table = plan.Table, Source = local.Location,
                    Rows = chunk.Select(e => JsonSerializer.SerializeToElement(e, t, RemoteJson.Options)).ToList(),
                });
                plan.Copied += r.Inserted;
            }
        }
        progress?.Invoke("Copying the audit history...");
        var audit = local.RecentAudit(int.MaxValue);
        for (var i = 0; i < audit.Count; i += 2000)
            report.AuditRows += server.Api.MigrateAudit(new MigrateAuditRequest { SourceKey = key, Rows = audit.OrderBy(a => a.Id).Skip(i).Take(2000).ToList() });
        server.Api.SetMeta(ServerSourceKey, key);
        foreach (var k in new[] { "ProjectStart", "PlannedFinish" })
            if (local.GetMeta(k) is { } v && server.Api.GetMeta(k) is null) server.Api.SetMeta(k, v);
        server.Cache.Invalidate();
        return report;
    }

    private static string Comparable(Entity e)
    {
        var c = EntityMeta.Clone(e);
        c.RowVersion = 0; c.UpdatedAt = default; c.UpdatedBy = "";
        foreach (var p in EntityMeta.Props(c.GetType()).Where(p => p.PropertyType == typeof(DateTime)))
            p.SetValue(c, TruncateMs((DateTime)p.GetValue(c)!));
        foreach (var p in EntityMeta.Props(c.GetType()).Where(p => p.PropertyType == typeof(DateTime?)))
            if (p.GetValue(c) is DateTime d) p.SetValue(c, TruncateMs(d));
        return RemoteJson.Serialize(c);
    }

    private static DateTime TruncateMs(DateTime d) => new(d.Ticks - d.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Unspecified);

    private static IEnumerable<List<Entity>> Chunks(List<Entity> rows)
    {
        var chunk = new List<Entity>();
        var size = 0;
        foreach (var e in rows)
        {
            var n = RemoteJson.Serialize(e).Length;
            if (chunk.Count > 0 && (size + n > MaxChunkChars || chunk.Count >= 1000)) { yield return chunk; chunk = new List<Entity>(); size = 0; }
            chunk.Add(e); size += n;
        }
        if (chunk.Count > 0) yield return chunk;
    }
}
