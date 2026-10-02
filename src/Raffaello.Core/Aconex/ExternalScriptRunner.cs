using System.Diagnostics;
using System.Text;

namespace Raffaello.Core.Aconex;

/// <summary>
/// Runs the external aconex_downloader.py (or any script) and streams stdout / stderr lines back.
/// The script is never bundled: its path and the Python interpreter come from Settings.
/// </summary>
public sealed class ExternalScriptRunner
{
    private Process? _process;
    public bool IsRunning => _process is { HasExited: false };

    public event Action<string>? Output;
    public event Action<int>? Exited;

    public static string BuildArguments(string scriptPath, string? inputExcel, string? docType, string? outputFolder)
    {
        var sb = new StringBuilder();
        sb.Append('"').Append(scriptPath).Append('"');
        if (!string.IsNullOrWhiteSpace(inputExcel)) sb.Append(" --input \"").Append(inputExcel).Append('"');
        if (!string.IsNullOrWhiteSpace(docType) && docType != "ALL") sb.Append(" --type ").Append(docType);
        if (!string.IsNullOrWhiteSpace(outputFolder)) sb.Append(" --out \"").Append(outputFolder).Append('"');
        return sb.ToString();
    }

    public Task<int> RunAsync(string python, string arguments, string? workingDir = null, CancellationToken ct = default)
    {
        if (IsRunning) throw new InvalidOperationException("A download is already running.");
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var psi = new ProcessStartInfo(python, arguments)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDir ?? Environment.CurrentDirectory,
        };
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) Output?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) Output?.Invoke("ERR " + e.Data); };
        p.Exited += (_, _) =>
        {
            var code = p.ExitCode;
            Exited?.Invoke(code);
            tcs.TrySetResult(code);
        };
        Output?.Invoke($"> {python} {arguments}");
        try
        {
            p.Start();
        }
        catch (Exception ex)
        {
            Output?.Invoke("ERR could not start: " + ex.Message);
            tcs.TrySetResult(-1);
            return tcs.Task;
        }
        _process = p;
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        ct.Register(Stop);
        return tcs.Task;
    }

    public void Stop()
    {
        try
        {
            if (IsRunning) { _process!.Kill(entireProcessTree: true); Output?.Invoke("Stopped by user."); }
        }
        catch (Exception ex) { Output?.Invoke("ERR stop: " + ex.Message); }
    }

    /// <summary>Lists files in the download folder whose name contains a known document number.</summary>
    public static Dictionary<string, string> MatchDownloads(string folder, IEnumerable<string> docNumbers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return result;
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList();
        foreach (var no in docNumbers)
        {
            var f = files.FirstOrDefault(x => Path.GetFileName(x).Contains(no, StringComparison.OrdinalIgnoreCase));
            if (f != null) result[no] = f;
        }
        return result;
    }
}
