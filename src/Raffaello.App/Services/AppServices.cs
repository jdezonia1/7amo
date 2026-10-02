using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;
using Raffaello.Core;
using Raffaello.Core.Chain;
using Raffaello.Core.Data;
using Raffaello.Core.Export;

namespace Raffaello.App.Services;

/// <summary>Holds the project data for the UI thread and raises <see cref="DataChanged"/> after every reload.</summary>
public sealed class DataService
{
    public ProjectService Project { get; }
    public event Action? DataChanged;
    public bool IsBusy { get; private set; }

    public DataService(ProjectService project) => Project = project;

    public async Task ReloadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await Task.Run(Project.Reload); }
        finally { IsBusy = false; }
        DataChanged?.Invoke();
    }

    /// <summary>Runs an audited write off the UI thread, then refreshes every screen.</summary>
    public async Task<bool> WriteAsync(Action<ProjectService> write, ToastService toasts, string? success = null)
    {
        try
        {
            await Task.Run(() => write(Project));
            DataChanged?.Invoke();
            if (success != null) toasts.Show(success, kind: ToastKind.Good);
            return true;
        }
        catch (ConcurrencyException ex)
        {
            toasts.Show("CHANGED BY SOMEONE ELSE", ex.Message, ToastKind.Warn);
            await ReloadAsync();
            return false;
        }
        catch (Exception ex)
        {
            toasts.Show("SAVE FAILED", StoreErrors.Describe(ex), ToastKind.Error);
            return false;
        }
    }

    public void RaiseChanged() => DataChanged?.Invoke();
}

public enum ToastKind { Info, Good, Warn, Error }

public sealed partial class Toast : ObservableObject
{
    public string Title { get; init; } = "";
    public string Message { get; init; } = "";
    public ToastKind Kind { get; init; }
    public string Status => Kind switch { ToastKind.Good => "OK", ToastKind.Warn => "CHECK", ToastKind.Error => "OVER", _ => "OPEN" };
}

public sealed class ToastService
{
    public ObservableCollection<Toast> Items { get; } = new();

    public void Show(string title, string message = "", ToastKind kind = ToastKind.Info, int seconds = 5)
    {
        void Add()
        {
            var t = new Toast { Title = title.ToUpperInvariant(), Message = message, Kind = kind };
            Items.Add(t);
            while (Items.Count > 4) Items.RemoveAt(0);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += (_, _) => { timer.Stop(); Items.Remove(t); };
            timer.Start();
        }
        if (Application.Current?.Dispatcher.CheckAccess() == true) Add();
        else Application.Current?.Dispatcher.Invoke(Add);
    }
}

public sealed class DialogService
{
    public const string ExcelFilter = "Excel / CSV|*.xlsx;*.xlsm;*.csv|All files|*.*";

    public string? OpenFile(string title, string filter = ExcelFilter)
    {
        var d = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return d.ShowDialog() == true ? d.FileName : null;
    }

    public string? SaveFile(string title, string defaultName, string filter = "Excel workbook|*.xlsx")
    {
        var d = new SaveFileDialog { Title = title, FileName = defaultName, Filter = filter, AddExtension = true };
        return d.ShowDialog() == true ? d.FileName : null;
    }

    public string? PickFolder(string title)
    {
        var d = new OpenFolderDialog { Title = title };
        return d.ShowDialog() == true ? d.FolderName : null;
    }

    public bool Confirm(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public static void OpenWithShell(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { /* no associated app */ }
    }
}

/// <summary>Excel export in the house format, with a toast and an option to open the file.</summary>
public sealed class ExportService
{
    private readonly DialogService _dialogs;
    private readonly ToastService _toasts;
    public ExportService(DialogService dialogs, ToastService toasts) { _dialogs = dialogs; _toasts = toasts; }

    public void Export(string defaultName, IEnumerable<ExportSheet> sheets, bool open = true)
    {
        var path = _dialogs.SaveFile("Export to Excel", $"{defaultName}_{DateTime.Now:yyyyMMdd_HHmm}.xlsx");
        if (path is null) return;
        try
        {
            ExcelExporter.Export(path, sheets);
            _toasts.Show("EXPORTED", Path.GetFileName(path), ToastKind.Good);
            if (open) DialogService.OpenWithShell(path);
        }
        catch (IOException ex)
        {
            _toasts.Show("EXPORT FAILED", ex.Message + " (is the file open in Excel?)", ToastKind.Error);
        }
    }
}

/// <summary>What the user is looking at right now: the screen and the selected chain line (context for Ask Raffaello).</summary>
public sealed partial class SelectionService : ObservableObject
{
    [ObservableProperty] private string _screen = "Welcome";
    [ObservableProperty] private ChainRow? _selectedLine;
}

/// <summary>Writes a presence heartbeat to the shared data file every 60 s and reads who else is online.</summary>
public sealed partial class PresenceService : ObservableObject
{
    private readonly DataService _data;
    private readonly SelectionService _selection;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(60) };

    [ObservableProperty] private string _othersOnline = "Only you";
    [ObservableProperty] private int _othersCount;
    [ObservableProperty] private DateTime? _lastSync;

    public PresenceService(DataService data, SelectionService selection)
    {
        _data = data; _selection = selection;
        _timer.Tick += async (_, _) => await BeatAsync();
    }

    public void Start() { _timer.Start(); _ = BeatAsync(); }

    public async Task BeatAsync()
    {
        try
        {
            var screen = _selection.Screen;
            var others = await Task.Run(() =>
            {
                _data.Project.Heartbeat(screen);
                return _data.Project.OthersOnline(TimeSpan.FromMinutes(3));
            });
            OthersCount = others.Count;
            OthersOnline = others.Count == 0 ? "Only you" : string.Join(", ", others.Select(o => $"{o.User} ({o.Screen})"));
            LastSync = DateTime.Now;
        }
        catch { OthersOnline = "Data file not reachable"; }
    }
}
