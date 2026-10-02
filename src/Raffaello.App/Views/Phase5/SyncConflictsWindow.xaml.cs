using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Raffaello.App.Services;
using Raffaello.App.ViewModels;
using Raffaello.Core.Remote;

namespace Raffaello.App.Views.Phase5;

public sealed partial class MergeFieldRow : ObservableObject
{
    public MergeField Field { get; }
    public MergeFieldRow(MergeField f) { Field = f; _useMine = f.Use == MergeSide.Mine; }
    public string Name => Field.Name;
    public string Base => Field.Base;
    public string Mine => Field.Mine;
    public string Theirs => Field.Theirs;
    public string Flag => Field.IsConflict ? "BOTH CHANGED" : Field.ChangedByMe ? "MINE" : "THEIRS";
    [ObservableProperty] private bool _useMine;
    partial void OnUseMineChanged(bool value) => Field.Use = value ? MergeSide.Mine : MergeSide.Theirs;
}

public sealed partial class SyncConflictsModel : ObservableObject
{
    private readonly RemoteProjectStore _store;
    public SyncConflictsModel(RemoteProjectStore store) { _store = store; Reload(); }

    public ObservableCollection<SyncConflict> Items { get; } = new();
    public ObservableCollection<MergeFieldRow> Fields { get; } = new();
    [ObservableProperty] private SyncConflict? _selected;
    [ObservableProperty] private string _overReason = "";
    [ObservableProperty] private string _message = "";
    public bool ShowReason => Selected?.Kind == ConflictKinds.Remaining;

    partial void OnSelectedChanged(SyncConflict? value)
    {
        Fields.Clear();
        if (value != null) foreach (var f in _store.MergePlan(value)) Fields.Add(new MergeFieldRow(f));
        OnPropertyChanged(nameof(ShowReason));
    }

    public void Reload()
    {
        Items.Clear();
        foreach (var c in _store.Conflicts) Items.Add(c);
        Selected = Items.FirstOrDefault();
        Message = Items.Count == 0 ? "No conflicts - everything is in sync." : "";
    }
}

public partial class SyncConflictsWindow : Window
{
    private readonly RemoteProjectStore _store;
    private readonly PageContext _ctx;
    private readonly SyncConflictsModel _vm;

    public SyncConflictsWindow(RemoteProjectStore store, PageContext ctx)
    {
        InitializeComponent();
        _store = store; _ctx = ctx;
        DataContext = _vm = new SyncConflictsModel(store);
    }

    private async void Do(Action<SyncConflict> action, string done)
    {
        if (_vm.Selected is not { } c) return;
        try
        {
            action(c);
            _vm.Reload();
            _vm.Message = done;
            await _ctx.Data.ReloadAsync();
        }
        catch (Exception ex)
        {
            _vm.Reload();
            _vm.Message = ex.Message;
        }
    }

    private void OnKeepMine(object sender, RoutedEventArgs e) => Do(c => _store.KeepMine(c, _vm.OverReason), "Your version was written to the server.");
    private void OnKeepTheirs(object sender, RoutedEventArgs e) => Do(_store.KeepTheirs, "Your change was dropped; the server version stays.");
    private void OnMerge(object sender, RoutedEventArgs e) => Do(c => _store.Merge(c, _vm.Fields.Select(f => f.Field)), "Merged and saved.");
    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
