using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.App.Services;

namespace Raffaello.App.ViewModels;

/// <summary>SETTINGS > UPDATES: installed version, pending changes from GitHub, UPDATE NOW (patch, not a full zip).</summary>
public sealed partial class UpdatesViewModel : ObservableObject
{
    private readonly UpdateService _svc = UpdateService.Instance;

    public UpdatesViewModel() { _ = Check(); }

    public string Installed => _svc.CurrentCommit is { Length: >= 7 } c
        ? $"{c[..7]}{(_svc.IsGitClone ? "  (git clone - use git pull)" : "")}"
        : _svc.CodeRoot is null ? "not installed by the updater (no code folder next to this exe)" : "unknown - run UPDATE_AND_RUN.bat once";
    public string Folder => _svc.CodeRoot ?? "-";
    public ObservableCollection<string> Changes { get; } = new();

    [ObservableProperty] private string _status = "Checking...";
    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private bool _updateAvailable;
    public bool CanUpdate => _svc.CanUpdate;

    [RelayCommand]
    private async Task Check()
    {
        if (IsChecking) return;
        IsChecking = true;
        Status = "Checking GitHub...";
        try
        {
            var r = await _svc.CheckAsync();
            Changes.Clear();
            foreach (var c in r.Changes) Changes.Add(c);
            UpdateAvailable = r.Ok && r.Behind != 0;
            Status = r.Message + (UpdateAvailable && !_svc.CanUpdate ? "  (update this copy with git pull / UPDATE_AND_RUN.bat)" : "");
        }
        finally { IsChecking = false; }
    }

    [RelayCommand]
    private void UpdateNow()
    {
        if (!_svc.CanUpdate) { Status = "This copy cannot update itself - use UPDATE_AND_RUN.bat (or git pull for a clone)."; return; }
        if (MessageBox.Show("Raffaello will close, download only the changed files, rebuild and start again (about a minute).\n\nYour data is not touched. Continue?",
                "Update Raffaello", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        if (_svc.LaunchUpdate()) Application.Current.Shutdown();
    }
}
