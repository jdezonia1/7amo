using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.App.Services;
using Raffaello.Core.Import;

namespace Raffaello.App.ViewModels;

/// <summary>Import wizard: pick a file and a kind, preview rows and validation issues, then commit in one transaction.</summary>
public sealed partial class ImportViewModel : ObservableObject
{
    private readonly DataService _data;
    private readonly DialogService _dialogs;
    private readonly ToastService _toasts;
    private TableData? _table;

    public ImportViewModel(DataService data, DialogService dialogs, ToastService toasts)
    {
        _data = data; _dialogs = dialogs; _toasts = toasts;
        Importers = ImporterRegistry.All;
    }

    public IReadOnlyList<IImporter> Importers { get; }
    public ObservableCollection<ImportIssue> Issues { get; } = new();

    [ObservableProperty] private IImporter? _importer;
    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private DataView? _preview;
    [ObservableProperty] private string _summary = "Choose a file to preview.";
    [ObservableProperty] private bool _canCommit;
    [ObservableProperty] private bool _isBusy;
    private ImportPreview? _result;

    public event Action<bool>? CloseRequested;

    public void Start(string? kind)
    {
        Importer = kind is null ? null : ImporterRegistry.ByKind(kind);
        FilePath = "";
        Preview = null;
        Issues.Clear();
        CanCommit = false;
        Summary = "Choose a file to preview.";
    }

    partial void OnImporterChanged(IImporter? value) { if (_table != null && value != null) Parse(); }

    [RelayCommand]
    private void Browse()
    {
        var f = _dialogs.OpenFile("Import Excel / CSV");
        if (f is null) return;
        FilePath = f;
        try
        {
            _table = TableReader.Read(f);
            if (Importer is null) Importer = ImporterRegistry.Guess(_table); else Parse();
            _data.Project.Settings.AddRecentFile(f);
            _data.Project.Settings.Save();
        }
        catch (IOException ex) { Summary = "Cannot read the file: " + ex.Message + " (close it in Excel?)"; }
        catch (Exception ex) { Summary = "Cannot read the file: " + ex.Message; }
    }

    private void Parse()
    {
        if (_table is null || Importer is null) return;
        _result = Importer.Parse(_table, _data.Project.Snapshot, _data.Project.Options);
        Preview = _result.Table.DefaultView;
        Issues.Clear();
        foreach (var i in _result.Issues.Take(500)) Issues.Add(i);
        Summary = $"{Path.GetFileName(FilePath)}: {_table.Rows.Count:N0} rows read. {_result.Summary} {_result.ErrorCount} errors, {_result.WarningCount} warnings.";
        CanCommit = _result.CanCommit;
    }

    [RelayCommand]
    private async Task Commit()
    {
        if (_result is null || !_result.CanCommit) return;
        IsBusy = true;
        var r = _result;
        var ok = await _data.WriteAsync(p => { r.Commit(p.Db); p.Reload(); }, _toasts, $"IMPORTED {r.Kind}: {r.ValidRows:N0} rows");
        IsBusy = false;
        if (ok) CloseRequested?.Invoke(true);
    }

    [RelayCommand] private void Cancel() => CloseRequested?.Invoke(false);
}
