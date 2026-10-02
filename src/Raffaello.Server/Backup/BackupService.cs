using System.Diagnostics;
using System.Globalization;
using Npgsql;

namespace Raffaello.Server.Backup;

/// <summary>
/// pg_dump / pg_restore wrapper. Backups are PostgreSQL custom-format dumps named raffaello_yyyyMMdd_HHmmss.dump in the
/// backup folder; retention deletes dumps older than N days but always keeps the newest few.
/// </summary>
public sealed class BackupRunner
{
    private readonly string _connectionString;
    private readonly ServerOptions _opt;
    private readonly string _folder;

    public BackupRunner(string connectionString, ServerOptions opt, string contentRoot)
    {
        _connectionString = connectionString; _opt = opt; _folder = opt.ResolveBackupFolder(contentRoot);
    }

    public string Folder => _folder;

    public string Backup()
    {
        Directory.CreateDirectory(_folder);
        var b = new NpgsqlConnectionStringBuilder(_connectionString);
        var file = Path.Combine(_folder, $"raffaello_{DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}.dump");
        var temp = file + ".part";
        Run(Tool("pg_dump"), b, "--format=custom", "--no-owner", "--file", temp, "--dbname", b.Database ?? "raffaello");
        File.Move(temp, file, overwrite: true);
        ApplyRetention();
        return file;
    }

    /// <summary>Restores a dump over the configured database (objects are dropped and re-created).</summary>
    public void Restore(string dumpFile, string? database = null)
    {
        if (!File.Exists(dumpFile)) throw new FileNotFoundException("Backup file not found.", dumpFile);
        var b = new NpgsqlConnectionStringBuilder(_connectionString);
        if (database != null) b.Database = database;
        Run(Tool("pg_restore"), b, "--clean", "--if-exists", "--no-owner", "--single-transaction", "--dbname", b.Database ?? "raffaello", dumpFile);
    }

    public IReadOnlyList<FileInfo> List() =>
        Directory.Exists(_folder) ? new DirectoryInfo(_folder).GetFiles("raffaello_*.dump").OrderByDescending(f => f.Name, StringComparer.Ordinal).ToList() : new List<FileInfo>();

    public int ApplyRetention()
    {
        var files = List();
        var cutoff = DateTime.Now.AddDays(-Math.Max(1, _opt.BackupRetentionDays));
        var removed = 0;
        foreach (var f in files.Skip(Math.Max(1, _opt.BackupKeepMin)).Where(f => f.LastWriteTime < cutoff))
        {
            try { f.Delete(); removed++; } catch (IOException) { /* in use - next night */ }
        }
        return removed;
    }

    private static void Run(string exe, NpgsqlConnectionStringBuilder b, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("--host"); psi.ArgumentList.Add(b.Host ?? "localhost");
        psi.ArgumentList.Add("--port"); psi.ArgumentList.Add(b.Port.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--username"); psi.ArgumentList.Add(b.Username ?? "postgres");
        psi.ArgumentList.Add("--no-password");
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (!string.IsNullOrEmpty(b.Password)) psi.Environment["PGPASSWORD"] = b.Password;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}.");
        var errTask = p.StandardError.ReadToEndAsync();
        _ = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        var err = errTask.GetAwaiter().GetResult();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(exe)} failed (exit {p.ExitCode}): {err.Trim()}");
    }

    /// <summary>pg_dump / pg_restore location: setting, PATH, then the usual install folders (highest version first).</summary>
    public string Tool(string name)
    {
        var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        if (!string.IsNullOrWhiteSpace(_opt.PgBinPath)) return Path.Combine(_opt.PgBinPath, exe);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var f = Path.Combine(dir.Trim('"'), exe);
            if (File.Exists(f)) return f;
        }
        var roots = OperatingSystem.IsWindows()
            ? new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL") }
            : new[] { "/usr/lib/postgresql" };
        foreach (var root in roots.Where(Directory.Exists))
            foreach (var v in Directory.GetDirectories(root).OrderByDescending(d => int.TryParse(Path.GetFileName(d), out var n) ? n : 0))
            {
                var f = Path.Combine(v, "bin", exe);
                if (File.Exists(f)) return f;
            }
        return exe;
    }
}

/// <summary>Runs the backup every night at <see cref="ServerOptions.BackupAt"/> and records the outcome in Meta.</summary>
public sealed class NightlyBackupService : BackgroundService
{
    private readonly BackupRunner _runner;
    private readonly ServerOptions _opt;
    private readonly NpgsqlDataSource _ds;
    private readonly ILogger<NightlyBackupService> _log;

    public NightlyBackupService(BackupRunner runner, ServerOptions opt, NpgsqlDataSource ds, ILogger<NightlyBackupService> log)
    {
        _runner = runner; _opt = opt; _ds = ds; _log = log;
    }

    public static DateTime NextRun(DateTime now, string at)
    {
        var t = TimeSpan.TryParseExact(at, @"hh\:mm", CultureInfo.InvariantCulture, out var ts) ? ts : new TimeSpan(2, 0, 0);
        var next = now.Date + t;
        return next <= now ? next.AddDays(1) : next;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opt.NightlyBackup) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = NextRun(DateTime.Now, _opt.BackupAt) - DateTime.Now;
            try { await Task.Delay(wait, stoppingToken); } catch (TaskCanceledException) { return; }
            string status;
            try
            {
                var file = await Task.Run(_runner.Backup, stoppingToken);
                status = $"OK {DateTime.Now:s} {Path.GetFileName(file)}";
                _log.LogInformation("Nightly backup written: {File}", file);
            }
            catch (Exception ex)
            {
                status = $"FAILED {DateTime.Now:s} {ex.Message}";
                _log.LogError(ex, "Nightly backup failed");
            }
            try
            {
                await using var c = await _ds.OpenConnectionAsync(stoppingToken);
                new Data.PgTx(c, null).Exec("""INSERT INTO "Meta" ("Key", "Value") VALUES ('Server:LastBackup', @v) ON CONFLICT ("Key") DO UPDATE SET "Value" = @v""", ("v", status));
            }
            catch (Exception ex) { _log.LogWarning(ex, "Could not record the backup status"); }
        }
    }
}
