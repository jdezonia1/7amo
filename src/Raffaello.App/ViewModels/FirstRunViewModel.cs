using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.App.Services;
using Raffaello.Core;
using Raffaello.Core.Remote;
using Raffaello.Core.Settings;

namespace Raffaello.App.ViewModels;

/// <summary>[phase6] First-run wizard: data source, documents folders, first imports, Aconex configuration, demo mode.</summary>
public sealed partial class FirstRunViewModel : ObservableObject
{
    private readonly ProjectService _project;
    private readonly DialogService _dialogs;
    private readonly AconexAutomationService _aconex;

    public FirstRunViewModel(ProjectService project, DialogService dialogs, AconexAutomationService aconex)
    {
        _project = project; _dialogs = dialogs; _aconex = aconex;
        var s = project.Settings;
        var remote = RemoteSettings.Load();
        UseServer = remote.IsServer;
        ServerUrl = remote.ServerUrl;
        DataFile = s.DataFilePath;
        DocumentsRoot = string.IsNullOrWhiteSpace(s.DocumentsRoot) ? ProjectFolders.DefaultRoot : s.DocumentsRoot;
        DemoMode = s.SeedDemoData;
        UserName = s.EffectiveUserName;
    }

    public event Action<bool>? CloseRequested;
    public bool NeedsRestart { get; private set; }

    [ObservableProperty] private string _userName = "";
    [ObservableProperty] private bool _useServer;
    [ObservableProperty] private string _serverUrl = "";
    [ObservableProperty] private string _dataFile = "";
    [ObservableProperty] private string _serverStatus = "";
    [ObservableProperty] private string _documentsRoot = "";
    [ObservableProperty] private string _foldersStatus = "";
    [ObservableProperty] private string _trackerStatus = "Not imported yet (optional - also on Rooms & Ledger).";
    [ObservableProperty] private string _contractStatus = "Not imported yet (optional - also on Contracts & BOQ).";
    [ObservableProperty] private string _contractNo = "";
    [ObservableProperty] private string _subcontractor = "";
    [ObservableProperty] private string _aconexStatus = "";
    [ObservableProperty] private bool _demoMode;
    [ObservableProperty] private bool _busy;

    public bool UseLocal { get => !UseServer; set => UseServer = !value; }
    partial void OnUseServerChanged(bool value) => OnPropertyChanged(nameof(UseLocal));

    [RelayCommand]
    private void BrowseData()
    {
        var f = _dialogs.SaveFile("Data file (put it on the shared drive for the whole team)", Path.GetFileName(DataFile), "Raffaello data|*.db|All files|*.*");
        if (f != null) DataFile = f;
    }

    [RelayCommand]
    private void BrowseDocuments() { if (_dialogs.PickFolder("Shared documents folder (WIR, MIR, packages, variation documents, Aconex screenshots)") is { } f) DocumentsRoot = f; }

    [RelayCommand]
    private async Task TestServer()
    {
        Busy = true;
        var settings = new RemoteSettings { Mode = DataSources.Server, ServerUrl = ServerUrl.Trim(), UseWindowsAuth = true };
        var (ok, msg) = await Task.Run(() => DataSourceFactory.Test(settings));
        ServerStatus = (ok ? "OK - " : "PROBLEM - ") + msg;
        Busy = false;
    }

    [RelayCommand]
    private void CreateFolders()
    {
        _project.Settings.DocumentsRoot = DocumentsRoot.Trim();
        var f = ProjectFolders.From(_project.Settings);
        var problems = f.Ensure();
        FoldersStatus = problems.Count == 0 ? "Folders ready:\n" + string.Join("\n", f.All.Select(x => $"  {x.Name,-20} {x.Path}")) : "Problems:\n" + string.Join("\n", problems);
    }

    [RelayCommand]
    private async Task ImportTracker()
    {
        var file = _dialogs.OpenFile("Tracker workbook (BRANDED_MEP_TRACKER)", "Tracker|*.xlsm;*.xlsx|All files|*.*");
        if (file is null) return;
        Busy = true;
        try
        {
            var r = await Task.Run(() => _project.Workflow.PreviewTracker(file, Raffaello.Core.Domain.Buildings.Branded));
            if (_dialogs.Confirm("Import tracker", r.Summary + "\n\nImport now?"))
                TrackerStatus = await Task.Run(() => _project.Workflow.CommitTracker(r));
        }
        catch (Exception ex) { TrackerStatus = "Could not read the tracker: " + ex.Message; }
        Busy = false;
    }

    [RelayCommand]
    private async Task ImportContract()
    {
        if (string.IsNullOrWhiteSpace(ContractNo) || string.IsNullOrWhiteSpace(Subcontractor)) { ContractStatus = "Type the subcontract no. and the subcontractor first (the workbook does not carry them)."; return; }
        var file = _dialogs.OpenFile("Contract link workbook");
        if (file is null) return;
        Busy = true;
        try
        {
            var (no, sub) = (ContractNo.Trim(), Subcontractor.Trim().ToUpperInvariant());
            var r = await Task.Run(() => _project.Workflow.PreviewContract(file, no, sub, null));
            if (_dialogs.Confirm("Import contract", r.Summary + "\n\nImport now?"))
            {
                await Task.Run(() => _project.Workflow.CommitContract(r));
                ContractStatus = $"{no} imported: {r.Items.Count} items, {r.Links.Count} BOQ links.";
            }
        }
        catch (Exception ex) { ContractStatus = "Could not read the workbook: " + ex.Message; }
        Busy = false;
    }

    [RelayCommand]
    private void OpenAconexConfig()
    {
        if (!File.Exists(_aconex.ConfigPath)) _aconex.SaveConfig();
        DialogService.OpenWithShell(_aconex.ConfigPath);
        AconexStatus = $"Edit and save {_aconex.ConfigPath}, then CHECK. Login happens in the browser the first time a lookup runs (your password is never stored in plain text).";
    }

    [RelayCommand]
    private void CheckAconex()
    {
        var problem = _aconex.ReloadConfig();
        AconexStatus = problem.Length == 0 ? "Aconex configuration is valid." : "Fix: " + problem;
    }

    [RelayCommand]
    private void Finish()
    {
        var s = _project.Settings;
        if (UserName.Trim().Length > 0) s.UserName = UserName.Trim();
        s.DocumentsRoot = DocumentsRoot.Trim();
        s.SeedDemoData = DemoMode;
        var remote = RemoteSettings.Load();
        var wasServer = remote.IsServer;
        remote.Mode = UseServer ? DataSources.Server : DataSources.Local;
        remote.ServerUrl = ServerUrl.Trim();
        remote.Save();
        if (!UseServer && !string.Equals(s.DataFilePath, DataFile, StringComparison.OrdinalIgnoreCase)) { s.DataFilePath = DataFile; NeedsRestart = true; }
        if (wasServer != remote.IsServer) NeedsRestart = true;
        s.FirstRunCompleted = true;
        s.Save();
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Skip()
    {
        _project.Settings.FirstRunCompleted = true;
        _project.Settings.Save();
        CloseRequested?.Invoke(false);
    }
}
