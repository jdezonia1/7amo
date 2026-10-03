using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.App.Services;
using Raffaello.Core.AconexWeb;

namespace Raffaello.App.ViewModels;

/// <summary>One of the two Aconex accounts in SETTINGS > ACONEX ACCOUNTS. The password is never bound or shown.</summary>
public sealed partial class AconexAccountRow : ObservableObject
{
    public AconexAccountRow(AconexProfile profile, string usedFor)
    {
        Profile = profile;
        UsedFor = usedFor;
    }

    public AconexProfile Profile { get; }
    public string Title => AconexProfiles.DisplayName(Profile);
    public string UsedFor { get; }
    /// <summary>Typed by the user to save a new login; never filled from the vault.</summary>
    [ObservableProperty] private string _userName = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _lastTest = "";
}

/// <summary>
/// Saved Aconex logins for the two accounts (MIR / WIR and WORKFLOWS / INVOICE UPLOAD): SAVE, TEST LOGIN, FORGET, and
/// the "Log in manually" fallback. Logins go to the DPAPI vault of the current Windows user only.
/// </summary>
public sealed partial class AconexAccountsViewModel : ObservableObject
{
    private readonly AconexAutomationService _svc;
    private readonly ToastService _toasts;
    private bool _loading;

    public AconexAccountsViewModel(AconexAutomationService svc, ToastService toasts)
    {
        _svc = svc;
        _toasts = toasts;
        Accounts = new ObservableCollection<AconexAccountRow>
        {
            new(AconexProfile.MirWir, "Used for: WIR / MIR register sync, WIR / MIR downloads, lookup by number."),
            new(AconexProfile.Workflows, "Used for: workflow status board, workflow lookup, workflow downloads, invoice package upload."),
        };
        svc.LoginAttention += t => Application.Current?.Dispatcher.BeginInvoke(() => Attention = t);
        Refresh();
    }

    public ObservableCollection<AconexAccountRow> Accounts { get; }

    /// <summary>"Log in manually": the app opens Aconex and waits for you, without typing the saved login.</summary>
    [ObservableProperty] private bool _manualLogin;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasAttention))] private string _attention = "";
    [ObservableProperty] private bool _isTesting;
    public bool HasAttention => Attention.Length > 0;

    public string StorageNote => _svc.Secrets.IsPersistent
        ? "Stored encrypted with Windows (DPAPI) for your Windows user on this PC only - not in settings files, the database or logs."
        : "No Windows encryption on this system: logins are kept for this session only.";

    public void Refresh()
    {
        _loading = true;
        try
        {
            foreach (var a in Accounts) a.Status = _svc.Account(a.Profile).Status;
            ManualLogin = !_svc.Config.Login.AutoFillStoredCredential;
            Attention = _svc.LoginAttentionText;
        }
        finally { _loading = false; }
    }

    partial void OnManualLoginChanged(bool value)
    {
        if (_loading) return;
        _svc.Config.Login.AutoFillStoredCredential = !value;
        try
        {
            _svc.SaveConfig();
            _toasts.Show(value ? "MANUAL ACONEX LOGIN" : "AUTOMATIC ACONEX LOGIN",
                value ? "Aconex opens and waits for you to log in." : "The saved logins are typed automatically (MFA / SSO stay with you).", ToastKind.Good);
        }
        catch (System.IO.IOException ex) { _toasts.Show("SAVE FAILED", ex.Message, ToastKind.Error); }
    }

    /// <summary>Called by the view with the PasswordBox content (never bound, never logged); the view clears the box.</summary>
    public void Save(AconexAccountRow row, string password)
    {
        if (string.IsNullOrWhiteSpace(row.UserName) || string.IsNullOrEmpty(password))
        {
            _toasts.Show("USER NAME AND PASSWORD", $"Type both for {row.Title}, then SAVE.", ToastKind.Warn);
            return;
        }
        try
        {
            _svc.SaveAccount(row.Profile, row.UserName, password);
            row.UserName = "";
            row.LastTest = "";
            _toasts.Show("ACONEX LOGIN SAVED", $"{row.Title} - encrypted for your Windows user. Press TEST LOGIN to check it.", ToastKind.Good, 8);
        }
        catch (Exception ex) when (ex is ArgumentException or System.IO.IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            _toasts.Show("SAVE FAILED", ex.Message, ToastKind.Error);
        }
        Refresh();
    }

    [RelayCommand]
    private async Task Test(AconexAccountRow? row)
    {
        if (row is null) return;
        if (_svc.IsBusy) { _toasts.Show("ACONEX IS BUSY", "Another lookup or download is running.", ToastKind.Warn); return; }
        IsTesting = true;
        row.LastTest = "Opening Aconex...";
        try
        {
            await _svc.TestLoginAsync(row.Profile);
            row.LastTest = $"LOGGED IN OK ({DateTime.Now:HH:mm})";
            _toasts.Show("ACONEX LOGIN OK", row.Title, ToastKind.Good);
        }
        catch (AconexLoginFailedException ex) { row.LastTest = "REFUSED - check the user name / password"; _toasts.Show("ACONEX LOGIN REFUSED", ex.Message, ToastKind.Error, 12); }
        catch (AconexLoginRequiredException ex) { row.LastTest = "NOT LOGGED IN"; _toasts.Show("ACONEX LOGIN NEEDED", ex.Message, ToastKind.Warn, 10); }
        catch (OperationCanceledException) { row.LastTest = "STOPPED"; }
        catch (Exception ex) { row.LastTest = "FAILED"; _toasts.Show("ACONEX FAILED", ex.Message.Split('\n')[0], ToastKind.Error, 10); }
        finally { IsTesting = false; Refresh(); }
    }

    [RelayCommand]
    private async Task Forget(AconexAccountRow? row)
    {
        if (row is null) return;
        if (_svc.IsBusy) { _toasts.Show("ACONEX IS BUSY", "Wait for the running lookup / download, then FORGET.", ToastKind.Warn); return; }
        await _svc.ForgetAccountAsync(row.Profile);
        row.LastTest = "";
        Refresh();
        _toasts.Show("ACONEX LOGIN FORGOTTEN", $"{row.Title}: saved login and browser session removed.", ToastKind.Good);
    }

    /// <summary>The user finished the login in the browser.</summary>
    [RelayCommand] private void Continue() => _svc.ContinueLogin();
}