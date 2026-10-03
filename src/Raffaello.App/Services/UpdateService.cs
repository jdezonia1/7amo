using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace Raffaello.App.Services;

/// <summary>Result of an update check: how many changes GitHub has that this install does not, and what they are.</summary>
public sealed record UpdateCheck(bool Ok, int Behind, IReadOnlyList<string> Changes, string Latest, string Message);

/// <summary>
/// Patch updates (03-Oct): finds the code\ folder this exe was built from (tools\update.ps1 + .raffaello_commit),
/// asks GitHub how many commits the branch is ahead, and runs tools\update.ps1, which downloads only the changed
/// files, rebuilds and restarts the app. A git clone or a copied exe (no code\ folder) is check-only.
/// </summary>
public sealed class UpdateService
{
    public const string Repo = "jdezonia1/7amo";
    public const string Branch = "claude/dazzling-turing-20kq62";
    public static UpdateService Instance { get; } = new();

    private static readonly HttpClient Http = CreateClient();

    public string? CodeRoot { get; }
    public string? InstalledCommit { get; }
    public bool IsGitClone => CodeRoot != null && Directory.Exists(Path.Combine(CodeRoot, ".git"));
    public bool CanUpdate => CodeRoot != null && !IsGitClone && File.Exists(Script);
    private string Script => Path.Combine(CodeRoot ?? "", "tools", "update.ps1");

    private UpdateService()
    {
        // ...\code\src\Raffaello.App\bin\Debug\net8.0-windows...\Raffaello.exe -> walk up to the folder with tools\update.ps1
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "tools", "update.ps1")) && Directory.Exists(Path.Combine(dir.FullName, "src")))
            {
                CodeRoot = dir.FullName;
                break;
            }
        var state = CodeRoot is null ? null : Path.Combine(CodeRoot, ".raffaello_commit");
        if (state != null && File.Exists(state)) InstalledCommit = File.ReadAllText(state).Trim();
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Raffaello-App");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>Installed commit; for a git clone it is read from .git (HEAD -> ref -> sha).</summary>
    public string? CurrentCommit => InstalledCommit ?? (IsGitClone ? GitHead(CodeRoot!) : null);

    private static string? GitHead(string root)
    {
        try
        {
            var git = Path.Combine(root, ".git");
            var head = File.ReadAllText(Path.Combine(git, "HEAD")).Trim();
            if (!head.StartsWith("ref: ", StringComparison.Ordinal)) return head;
            var refName = head[5..];
            var loose = Path.Combine(git, refName.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(loose)) return File.ReadAllText(loose).Trim();
            var packed = Path.Combine(git, "packed-refs");
            return File.Exists(packed)
                ? File.ReadLines(packed).Select(l => l.Split(' ')).FirstOrDefault(p => p.Length == 2 && p[1] == refName)?[0]
                : null;
        }
        catch { return null; }
    }

    /// <summary>Asks GitHub for the branch head and, when it differs, the commits this install is missing.</summary>
    public async Task<UpdateCheck> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var headDoc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/commits/{Branch}", ct));
            var latest = headDoc.RootElement.GetProperty("sha").GetString() ?? "";
            var current = CurrentCommit;
            if (current is null) return new(true, -1, Array.Empty<string>(), latest, "Installed version unknown - run UPDATE_AND_RUN.bat once.");
            if (string.Equals(current, latest, StringComparison.OrdinalIgnoreCase)) return new(true, 0, Array.Empty<string>(), latest, "Up to date.");
            using var cmp = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/compare/{current}...{latest}", ct));
            var r = cmp.RootElement;
            var behind = r.TryGetProperty("ahead_by", out var a) ? a.GetInt32() : 0;
            var changes = r.TryGetProperty("commits", out var commits)
                ? commits.EnumerateArray().Select(c => (c.GetProperty("commit").GetProperty("message").GetString() ?? "").Split('\n')[0].Trim()).Reverse().ToList()
                : new List<string>();
            var status = r.TryGetProperty("status", out var s) ? s.GetString() : "";
            return status == "ahead"
                ? new(true, behind, changes, latest, $"{behind} update(s) available.")
                : new(true, -1, changes, latest, "This version is not on the update branch - a full update will be downloaded.");
        }
        catch (Exception ex) { return new(false, 0, Array.Empty<string>(), "", "Could not check for updates (offline?): " + ex.Message); }
    }

    /// <summary>Starts tools\update.ps1 in a console window (it waits for this process to exit), then the app closes.</summary>
    public bool LaunchUpdate()
    {
        if (!CanUpdate) return false;
        var root = Path.GetDirectoryName(CodeRoot!.TrimEnd(Path.DirectorySeparatorChar)) ?? CodeRoot!;
        Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{Script}\" -Code \"{CodeRoot}\" -Root \"{root}\" -WaitPid {Environment.ProcessId}")
        { UseShellExecute = true });
        return true;
    }
}
