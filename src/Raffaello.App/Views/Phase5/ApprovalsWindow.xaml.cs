using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Raffaello.App.ViewModels;
using Raffaello.Core.Domain;
using Raffaello.Core.Remote;

namespace Raffaello.App.Views.Phase5;

public sealed partial class ApprovalsModel : ObservableObject
{
    private readonly RemoteProjectStore _store;
    public ApprovalsModel(RemoteProjectStore store) { _store = store; }

    public ObservableCollection<SubInvoice> Invoices { get; } = new();
    public ObservableCollection<ApprovalStampDto> Stamps { get; } = new();
    [ObservableProperty] private SubInvoice? _selected;
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private string _message = "";

    partial void OnSelectedChanged(SubInvoice? value) => LoadStamps();

    public void Load()
    {
        var keep = Selected?.Id;
        Invoices.Clear();
        foreach (var i in _store.All<SubInvoice>().Where(i => !i.Locked).OrderByDescending(i => i.UpdatedAt)) Invoices.Add(i);
        Selected = Invoices.FirstOrDefault(i => i.Id == keep) ?? Invoices.FirstOrDefault();
        if (Invoices.Count == 0) Message = "No open invoices.";
    }

    public void LoadStamps()
    {
        Stamps.Clear();
        if (Selected is null) return;
        try { foreach (var s in _store.Stamps(Selected)) Stamps.Add(s); }
        catch (Exception ex) { Message = ex.Message; }
    }

    public void Stamp(string stage)
    {
        if (Selected is null) return;
        try
        {
            var s = _store.Stamp(Selected, stage, Note.Trim());
            Message = $"{Selected.Title}: {s.Stage} by {s.By} at {s.At:HH:mm}.";
            Note = "";
            Load();
            LoadStamps();
        }
        catch (Exception ex) { Message = ex.Message; }
    }
}

public partial class ApprovalsWindow : Window
{
    private readonly ApprovalsModel _vm;
    private readonly PageContext _ctx;

    public ApprovalsWindow(RemoteProjectStore store, PageContext ctx)
    {
        InitializeComponent();
        _ctx = ctx;
        DataContext = _vm = new ApprovalsModel(store);
        Loaded += (_, _) => _vm.Load();
        Closed += async (_, _) => await _ctx.Data.ReloadAsync();
    }

    private void OnChecked(object sender, RoutedEventArgs e) => _vm.Stamp(ApprovalStages.Checked);
    private void OnApproved(object sender, RoutedEventArgs e) => _vm.Stamp(ApprovalStages.Approved);
    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
