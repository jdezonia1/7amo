using Microsoft.Data.Sqlite;
using Raffaello.Core.AconexWeb;
using Raffaello.Core.Data;
using Raffaello.Core.Materials;
using Raffaello.Core.Settings;
using Raffaello.Core.Variations;

namespace Raffaello.Core.Assistant.Mcp;

/// <summary>
/// The data the MCP server reads, opened so that the real data file can never be changed: the file is opened READ-ONLY and copied
/// (SQLite online backup) into a private snapshot in %TEMP%; every tool reads the snapshot. The snapshot is refreshed when the data
/// file changes (checked at most every <see cref="RefreshEvery"/>), and deleted on dispose.
/// </summary>
public sealed class McpDataSource : IDisposable
{
    private readonly AppSettings _template;
    private readonly AssistantSettings _assistant;
    private readonly string _user;
    private readonly string _snapDir;
    private string? _snapPath;
    private DateTime _sourceStamp;
    private DateTime _lastCheck;
    private McpToolCatalog? _catalog;

    public string SourcePath { get; }
    public TimeSpan RefreshEvery { get; set; } = TimeSpan.FromSeconds(15);
    public AssistantData? Data { get; private set; }
    public DateTime SnapshotAt { get; private set; }
    public TextWriter Log { get; set; } = TextWriter.Null;

    public McpDataSource(string sourcePath, AppSettings? settings = null, AssistantSettings? assistant = null, string? user = null, string? snapshotFolder = null)
    {
        SourcePath = Path.GetFullPath(sourcePath);
        _template = settings ?? new AppSettings();
        _assistant = assistant ?? new AssistantSettings();
        _user = string.IsNullOrWhiteSpace(user) ? _template.EffectiveUserName : user!;
        _snapDir = snapshotFolder ?? Path.Combine(Path.GetTempPath(), "Raffaello", "mcp");
    }

    /// <summary>The tool catalog over a current snapshot (refreshed when the data file changed).</summary>
    public McpToolCatalog Catalog()
    {
        var now = DateTime.UtcNow;
        if (_catalog is null || now - _lastCheck >= RefreshEvery)
        {
            _lastCheck = now;
            var stamp = Stamp();
            if (_catalog is null || stamp != _sourceStamp)
            {
                Load();
                _sourceStamp = stamp;
            }
        }
        return _catalog!;
    }

    private DateTime Stamp()
    {
        if (!File.Exists(SourcePath)) throw new FileNotFoundException("Raffaello data file not found: " + SourcePath, SourcePath);
        var t = File.GetLastWriteTimeUtc(SourcePath);
        var wal = SourcePath + "-wal";
        if (File.Exists(wal)) { var w = File.GetLastWriteTimeUtc(wal); if (w > t) t = w; }
        return t;
    }

    private void Load()
    {
        Directory.CreateDirectory(_snapDir);
        var snap = Path.Combine(_snapDir, $"snapshot-{Environment.ProcessId}-{DateTime.UtcNow:HHmmssfff}.db");
        CopyReadOnly(SourcePath, snap);

        var app = Clone(_template);
        app.DataFilePath = snap;
        app.SeedDemoData = false;
        app.UserName = _user;
        var project = new ProjectService(app, s => new Db(s.DataFilePath, s.EffectiveUserName));
        project.Initialize();
        var db = (Db)project.Store;
        var mats = new SqliteMaterialsStore(() => db);
        var aconex = new SqliteAconexStore(snap, _user);
        var variations = new SqliteVariationStore(snap, _user);
        var docs = new Raffaello.Core.Documents.SqliteDocumentStore(() => db);
        var store = new SqliteAssistantStore(snap, _user);
        store.EnsureSchema();
        Data = new AssistantData
        {
            Project = project, Store = store, Settings = _assistant,
            Materials = () => mats.Load(),
            Aconex = () => aconex,
            Variations = () => variations,
            Documents = new Raffaello.Core.Wiring.ArchiveDocumentSearch(() => docs),
            ContractDocs = () => docs,
            DraftFolder = Path.Combine(_snapDir, "drafts"),   // never used: the writing tools are not offered
        };
        _catalog = new McpToolCatalog(Data);
        var old = _snapPath;
        _snapPath = snap;
        SnapshotAt = DateTime.Now;
        Log.WriteLine($"[raffaello-mcp] snapshot of {SourcePath} taken ({project.Snapshot.Rooms.Count} rooms, {project.Snapshot.Claims.Count} claim lines)");
        if (old != null) DeleteSnapshot(old);
    }

    /// <summary>Copies a SQLite file through a read-only connection (online backup: consistent even while the app writes).</summary>
    public static void CopyReadOnly(string source, string target)
    {
        try
        {
            var srcCs = new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 15 }.ToString();
            var dstCs = new SqliteConnectionStringBuilder { DataSource = target, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
            using var src = new SqliteConnection(srcCs);
            src.Open();
            using var dst = new SqliteConnection(dstCs);
            dst.Open();
            src.BackupDatabase(dst);
        }
        catch (SqliteException)
        {
            // a read-only folder without the -shm file cannot be opened in WAL mode: copy the files instead (never written)
            SqliteConnection.ClearAllPools();
            File.Copy(source, target, true);
            if (File.Exists(source + "-wal")) File.Copy(source + "-wal", target + "-wal", true);
        }
    }

    private static AppSettings Clone(AppSettings s)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(s);
        return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
    }

    private void DeleteSnapshot(string path)
    {
        try
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.WriteLine("[raffaello-mcp] could not delete " + path + ": " + ex.Message); }
    }

    public void Dispose()
    {
        if (_snapPath != null) DeleteSnapshot(_snapPath);
        _snapPath = null;
    }
}