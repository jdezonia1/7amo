using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.App.Services;
using Raffaello.Core.Aconex;
using Raffaello.Core.Domain;
using Raffaello.Core.Export;

namespace Raffaello.App.ViewModels;

public sealed class AconexRow
{
    public required AconexDoc Doc { get; init; }
    public string DocNo => Doc.DocNo;
    public string Title => Doc.Title;
    public string DocType => Doc.DocType;
    public string Revision => Doc.Revision;
    public string ConsultantStatus => Doc.Status;
    public string Downloaded => Doc.DownloadedAt?.ToString("dd MMM yy") ?? "";
    public string Status => Doc.Queued ? "QUEUED" : Doc.DownloadedAt.HasValue ? "DOWNLOADED" : "OPEN";
    public string LocalPath => Doc.LocalPath;
}

/// <summary>Download queue that drives the external aconex_downloader.py and keeps a register of downloaded documents.</summary>
public sealed partial class AconexViewModel : PageViewModel
{
    private readonly ExternalScriptRunner _runner = new();

    public AconexViewModel(PageContext ctx) : base(ctx)
    {
        _runner.Output += line => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Log.Add($"{DateTime.Now:HH:mm:ss}  {line}");
            while (Log.Count > 2000) Log.RemoveAt(0);
        });
        _runner.Exited += code => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            IsRunning = false;
            Ctx.Toasts.Show(code == 0 ? "ACONEX RUN FINISHED" : "ACONEX RUN FAILED", $"exit code {code}", code == 0 ? ToastKind.Good : ToastKind.Error);
            _ = ScanFolder();
        });
    }

    public override string Key => "Aconex";
    public override string Title => "ACONEX";
    public override string Subtitle => "Document register download queue (ksa1.aconex.com) via aconex_downloader.py";
    public override bool ShowFilterBar => false;
    protected override bool UsesFilter => false;

    public ObservableCollection<AconexRow> Docs { get; } = new();
    public ObservableCollection<string> Log { get; } = new();
    public string[] Types { get; } = { "ALL", "WIR", "MIR", "SDW", "MAT" };
    public string[] Views { get; } = { "ALL", "QUEUED", "NOT DOWNLOADED", "DOWNLOADED" };

    [ObservableProperty] private string _typeFilter = "ALL";
    [ObservableProperty] private string _viewFilter = "ALL";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private AconexRow? _selected;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _pythonPath = "";
    [ObservableProperty] private string _scriptPath = "";
    [ObservableProperty] private string _inputExcel = "";
    [ObservableProperty] private string _downloadFolder = "";
    [ObservableProperty] private string _counts = "";

    partial void OnTypeFilterChanged(string value) => Refresh();
    partial void OnViewFilterChanged(string value) => Refresh();
    partial void OnSearchChanged(string value) => Refresh();

    protected override void Refresh()
    {
        var st = Project.Settings;
        if (string.IsNullOrEmpty(PythonPath)) PythonPath = st.PythonPath;
        if (string.IsNullOrEmpty(ScriptPath)) ScriptPath = st.AconexScriptPath;
        if (string.IsNullOrEmpty(InputExcel)) InputExcel = st.AconexInputExcel;
        if (string.IsNullOrEmpty(DownloadFolder)) DownloadFolder = st.AconexDownloadFolder;
        var docs = Project.Snapshot.AconexDocs.AsEnumerable();
        if (TypeFilter != "ALL") docs = docs.Where(d => d.DocType == TypeFilter);
        docs = ViewFilter switch
        {
            "QUEUED" => docs.Where(d => d.Queued),
            "NOT DOWNLOADED" => docs.Where(d => !d.DownloadedAt.HasValue),
            "DOWNLOADED" => docs.Where(d => d.DownloadedAt.HasValue),
            _ => docs,
        };
        if (!string.IsNullOrWhiteSpace(Search)) docs = docs.Where(d => d.DocNo.Contains(Search, StringComparison.OrdinalIgnoreCase) || d.Title.Contains(Search, StringComparison.OrdinalIgnoreCase));
        Docs.Clear();
        foreach (var d in docs.OrderByDescending(d => d.Queued).ThenBy(d => d.DownloadedAt.HasValue).ThenBy(d => d.DocNo)) Docs.Add(new AconexRow { Doc = d });
        var all = Project.Snapshot.AconexDocs;
        Counts = $"{all.Count:N0} IN REGISTER  |  {all.Count(d => d.DownloadedAt.HasValue):N0} DOWNLOADED  |  {all.Count(d => d.Queued):N0} QUEUED  |  {all.Count(d => !d.DownloadedAt.HasValue):N0} MISSING";
    }

    private void SaveSettings()
    {
        var st = Project.Settings;
        st.PythonPath = PythonPath; st.AconexScriptPath = ScriptPath; st.AconexInputExcel = InputExcel; st.AconexDownloadFolder = DownloadFolder;
        st.Save();
    }

    [RelayCommand]
    private async Task QueueMissing()
    {
        var missing = Docs.Where(d => !d.Doc.DownloadedAt.HasValue && !d.Doc.Queued).Select(d => d.Doc).ToList();
        if (missing.Count == 0) { Ctx.Toasts.Show("NOTHING TO QUEUE"); return; }
        await Ctx.Data.WriteAsync(p => p.SetQueued(missing, true), Ctx.Toasts, $"{missing.Count} documents queued");
    }

    [RelayCommand]
    private async Task ClearQueue()
    {
        var queued = Project.Snapshot.AconexDocs.Where(d => d.Queued).ToList();
        if (queued.Count > 0) await Ctx.Data.WriteAsync(p => p.SetQueued(queued, false), Ctx.Toasts, "Queue cleared");
    }

    [RelayCommand]
    private void ExportQueue()
    {
        var queued = Project.Snapshot.AconexDocs.Where(d => d.Queued).ToList();
        if (queued.Count == 0) { Ctx.Toasts.Show("QUEUE IS EMPTY", "Queue documents first.", ToastKind.Warn); return; }
        var path = Ctx.Dialogs.SaveFile("Save the queue as the downloader's input Excel", "ACONEX_QUEUE.xlsx");
        if (path is null) return;
        ExcelExporter.Export(path, new ExportSheet
        {
            Name = "QUEUE", Columns = new() { new("DOCUMENT NO"), new("TYPE"), new("TITLE", Width: 60) },
            Rows = queued.Select(d => new object?[] { d.DocNo, d.DocType, d.Title }).ToList(),
        });
        InputExcel = path;
        SaveSettings();
        Ctx.Toasts.Show("QUEUE SAVED", $"{queued.Count} document numbers -> {Path.GetFileName(path)}", ToastKind.Good);
    }

    [RelayCommand]
    private async Task Run()
    {
        SaveSettings();
        if (string.IsNullOrWhiteSpace(ScriptPath) || !File.Exists(ScriptPath))
        {
            Ctx.Toasts.Show("SCRIPT NOT FOUND", "Set the path to aconex_downloader.py (Browse).", ToastKind.Warn);
            return;
        }
        IsRunning = true;
        Log.Clear();
        var args = ExternalScriptRunner.BuildArguments(ScriptPath, InputExcel, TypeFilter, DownloadFolder);
        await _runner.RunAsync(PythonPath, args, Path.GetDirectoryName(ScriptPath));
    }

    [RelayCommand] private void Stop() => _runner.Stop();

    [RelayCommand]
    private async Task ScanFolder()
    {
        if (string.IsNullOrWhiteSpace(DownloadFolder)) { Ctx.Toasts.Show("NO DOWNLOAD FOLDER", "Pick the folder the downloader saves into.", ToastKind.Warn); return; }
        var docs = Project.Snapshot.AconexDocs.Where(d => !d.DownloadedAt.HasValue || d.Queued).Select(d => d.DocNo).ToList();
        var found = await Task.Run(() => ExternalScriptRunner.MatchDownloads(DownloadFolder, docs));
        if (found.Count == 0) { Ctx.Toasts.Show("NO NEW FILES", "No file names match a document number in the register."); return; }
        await Ctx.Data.WriteAsync(p => p.MarkDownloaded(found.Select(kv => (kv.Key, kv.Value))), Ctx.Toasts, $"{found.Count} documents registered");
    }

    [RelayCommand] private void BrowseScript() { var f = Ctx.Dialogs.OpenFile("aconex_downloader.py", "Python script|*.py|All files|*.*"); if (f != null) { ScriptPath = f; SaveSettings(); } }
    [RelayCommand] private void BrowsePython() { var f = Ctx.Dialogs.OpenFile("Python interpreter", "python.exe|python*.exe|All files|*.*"); if (f != null) { PythonPath = f; SaveSettings(); } }
    [RelayCommand] private void BrowseInput() { var f = Ctx.Dialogs.OpenFile("Excel with document numbers"); if (f != null) { InputExcel = f; SaveSettings(); } }
    [RelayCommand] private void BrowseFolder() { var f = Ctx.Dialogs.PickFolder("Download folder"); if (f != null) { DownloadFolder = f; SaveSettings(); } }
    [RelayCommand] private void OpenDoc() { if (Selected?.LocalPath is { Length: > 0 } p) DialogService.OpenWithShell(p); }

    public override IEnumerable<ExportSheet> ExportCurrentView()
    {
        yield return new ExportSheet
        {
            Name = "ACONEX REGISTER", Title = "ACONEX DOCUMENT REGISTER",
            Columns = new() { new("DOCUMENT NO"), new("TYPE"), new("REV"), new("TITLE", Width: 60), new("CONSULTANT STATUS"), new("DOWNLOADED"), new("LOCAL PATH", Width: 50) },
            Rows = Docs.Select(d => new object?[] { d.DocNo, d.DocType, d.Revision, d.Title, d.ConsultantStatus, d.Doc.DownloadedAt, d.LocalPath }).ToList(),
        };
    }
}
