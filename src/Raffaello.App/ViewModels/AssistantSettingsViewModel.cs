using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.App.Resources;
using Raffaello.App.Services;
using Raffaello.App.Services.Assistant;
using Raffaello.Core.Ai;
using Raffaello.Core.Assistant;
using Raffaello.Core.Localization;
using Raffaello.Core.Notify;

namespace Raffaello.App.ViewModels;

public sealed partial class NotifyRuleRow : ObservableObject
{
    public long Id { get; init; }
    [ObservableProperty] private string _eventKind = NotifyEvents.All;
    [ObservableProperty] private string _channel = NotifyChannels.InApp;
    [ObservableProperty] private string _minSeverity = "CHECK";
    [ObservableProperty] private string _quietFrom = "";
    [ObservableProperty] private string _quietTo = "";
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private bool _enabled = true;
}

public sealed record Choice(string Value, string Label);

/// <summary>
/// [assistant] Settings > ASSISTANT, LANGUAGE AND NOTIFICATIONS: interface language, API key (DPAPI), project-data and cloud-reading
/// switches, morning brief, channels (in-app, Windows, SMTP e-mail, Teams webhook, WhatsApp webhook) and per-user rules.
/// Secret fields are never shown again: leave them empty to keep the saved value, type "-" to remove it.
/// </summary>
public sealed partial class AssistantSettingsViewModel : ObservableObject
{
    private readonly AssistantHost _host;
    private readonly ToastService _toasts;
    private readonly Func<AssistantNotificationService?> _notify;

    public AssistantSettingsViewModel(AssistantHost host, ToastService toasts, Func<AssistantNotificationService?> notify)
    {
        _host = host; _toasts = toasts; _notify = notify;
        Load();
    }

    public Choice[] Languages { get; } = { new(Loc.English, "English"), new(Loc.Arabic, "العربية (Arabic)") };
    public string[] Events { get; } = NotifyEvents.Kinds;
    public string[] ChannelCodes { get; } = NotifyChannels.All;
    public string[] Severities { get; } = { "OK", "OPEN", "DUE", "CHECK", "OVER" };
    public string[] Efforts { get; } = { "low", "medium", "high", "xhigh", "max" };

    [ObservableProperty] private string _language = Loc.English;
    [ObservableProperty] private string _apiKey = "";
    [ObservableProperty] private string _keyStatus = "";
    [ObservableProperty] private string _model = AnthropicClient.DefaultModel;
    [ObservableProperty] private bool _allowRead = true;
    [ObservableProperty] private bool _cloudReading;
    [ObservableProperty] private string _maxTokens = "16000";
    [ObservableProperty] private bool _inAppToasts = true;
    [ObservableProperty] private bool _windowsToasts = true;
    [ObservableProperty] private string _notifyEvery = "15";
    [ObservableProperty] private bool _showBriefFirst = true;
    [ObservableProperty] private string _briefTime = "07:30";
    [ObservableProperty] private bool _briefClaudeSummary;
    [ObservableProperty] private string _smtpHost = "";
    [ObservableProperty] private string _smtpPort = "587";
    [ObservableProperty] private bool _smtpSsl = true;
    [ObservableProperty] private string _smtpUser = "";
    [ObservableProperty] private string _smtpPassword = "";
    [ObservableProperty] private string _smtpFrom = "";
    [ObservableProperty] private string _emailTo = "";
    [ObservableProperty] private string _teamsUrl = "";
    [ObservableProperty] private string _whatsAppUrl = "";
    [ObservableProperty] private string _whatsAppTo = "";
    [ObservableProperty] private string _whatsAppTemplate = "";
    [ObservableProperty] private string _whatsAppHeaderName = "";
    [ObservableProperty] private string _whatsAppHeaderValue = "";
    [ObservableProperty] private string _secretsStatus = "";
    [ObservableProperty] private string _testResult = "";
    [ObservableProperty] private NotifyRuleRow? _selectedRule;

    // [claude-login] provider + Claude Code + Claude Desktop
    public Choice[] Providers { get; } =
    {
        new(AssistantProviders.Auto, "Auto - API key if saved, else your Claude login, else offline"),
        new(AssistantProviders.ApiKey, "API key - Anthropic API key (confirm cards for writes)"),
        new(AssistantProviders.ClaudeLogin, "Claude login - your Claude subscription through Claude Code (read-only)"),
        new(AssistantProviders.Offline, "Offline - local answers only, nothing leaves this PC"),
    };
    [ObservableProperty] private string _provider = AssistantProviders.Auto;
    [ObservableProperty] private string _routeText = "";
    [ObservableProperty] private string _claudeStatusText = "";
    [ObservableProperty] private string _claudeCodePath = "";
    [ObservableProperty] private string _claudeCodeModel = "";
    [ObservableProperty] private string _claudeTimeout = "240";
    [ObservableProperty] private bool _isCheckingClaude;
    [ObservableProperty] private bool _showDesktop;
    [ObservableProperty] private string _desktopSnippet = "";
    public string DesktopInstructions => ClaudeDesktopConfig.Instructions;
    public ObservableCollection<NotifyRuleRow> Rules { get; } = new();

    public void Load()
    {
        var s = _host.Settings;
        Language = Loc.Normalize(s.Language);
        Model = _host.Session.Model;
        AllowRead = s.AllowReadProjectData;
        CloudReading = s.CloudDocumentReading;
        MaxTokens = s.MaxTokens.ToString(System.Globalization.CultureInfo.InvariantCulture);
        InAppToasts = s.InAppToasts; WindowsToasts = s.WindowsToasts; NotifyEvery = s.NotifyEveryMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ShowBriefFirst = s.ShowBriefFirst; BriefTime = s.BriefTime; BriefClaudeSummary = s.BriefClaudeSummary;
        SmtpHost = s.SmtpHost; SmtpPort = s.SmtpPort.ToString(System.Globalization.CultureInfo.InvariantCulture); SmtpSsl = s.SmtpSsl; SmtpUser = s.SmtpUser; SmtpFrom = s.SmtpFrom; EmailTo = s.EmailTo;
        WhatsAppTo = s.WhatsAppTo; WhatsAppTemplate = s.WhatsAppTemplate; WhatsAppHeaderName = s.WhatsAppHeaderName;
        ApiKey = SmtpPassword = TeamsUrl = WhatsAppUrl = WhatsAppHeaderValue = "";
        Provider = AssistantProviders.Normalize(s.Provider);
        ClaudeCodePath = s.ClaudeCodePath; ClaudeCodeModel = s.ClaudeCodeModel;
        ClaudeTimeout = s.ClaudeCodeTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Rules.Clear();
        try
        {
            var rules = _host.Store.Rules(_host.Data.User);
            foreach (var r in rules.Count > 0 ? rules : NotificationRouter.DefaultRules(_host.Data.User))
                Rules.Add(new NotifyRuleRow { Id = r.Id, EventKind = r.EventKind, Channel = r.Channel, MinSeverity = r.MinSeverity, QuietFrom = r.QuietFrom, QuietTo = r.QuietTo, Address = r.Address, Enabled = r.Enabled });
        }
        catch (Exception ex) { SecretsStatus = ex.Message; }
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        KeyStatus = _host.KeySource switch
        {
            ApiKeys.Source.Vault => Loc.T("Set_KeySaved") + "  " + ApiKeys.Mask(_host.Key),
            ApiKeys.Source.Environment => Loc.T("Set_KeyEnv"),
            ApiKeys.Source.LegacySettings => Loc.T("Set_KeyLegacy"),
            _ => Loc.T("Set_KeyNone"),
        };
        string Has(string name) => string.IsNullOrEmpty(_host.Vault.Get(name)) ? "-" : "saved";
        ClaudeStatusText = _host.ClaudeStatus.Message;
        var route = _host.Route();
        RouteText = "Next question will use: " + route.Route switch
        {
            AssistantRoute.Api => "API key",
            AssistantRoute.ClaudeCode => "Claude login (Claude Code)",
            _ => "Offline",
        } + (route.Reason.Length > 0 ? "  -  " + route.Reason : "");
        SecretsStatus = $"SMTP password: {Has(SecretNames.SmtpPassword)}  |  Teams URL: {Has(SecretNames.TeamsWebhook)}  |  WhatsApp URL: {Has(SecretNames.WhatsAppWebhook)}"
                        + (_host.Vault.IsPersistent ? "" : "  |  (no DPAPI here: secrets kept for this session only)");
    }

    private void SetSecret(string name, string typed)
    {
        var t = typed.Trim();
        if (t.Length == 0) return;                       // keep
        _host.Vault.Set(name, t == "-" ? "" : t);        // "-" removes
    }

    [RelayCommand]
    private void Save()
    {
        var s = _host.Settings;
        var langChanged = Loc.Normalize(s.Language) != Loc.Normalize(Language);
        s.Language = Loc.Normalize(Language);
        s.AllowReadProjectData = AllowRead;
        s.CloudDocumentReading = CloudReading;
        if (int.TryParse(MaxTokens, out var mt)) s.MaxTokens = Math.Clamp(mt, 1024, 64000);
        s.InAppToasts = InAppToasts; s.WindowsToasts = WindowsToasts;
        if (int.TryParse(NotifyEvery, out var ne)) s.NotifyEveryMinutes = Math.Clamp(ne, 2, 240);
        s.ShowBriefFirst = ShowBriefFirst; s.BriefTime = BriefTime.Trim(); s.BriefClaudeSummary = BriefClaudeSummary;
        s.SmtpHost = SmtpHost.Trim(); if (int.TryParse(SmtpPort, out var port)) s.SmtpPort = port; s.SmtpSsl = SmtpSsl; s.SmtpUser = SmtpUser.Trim(); s.SmtpFrom = SmtpFrom.Trim(); s.EmailTo = EmailTo.Trim();
        s.WhatsAppTo = WhatsAppTo.Trim(); s.WhatsAppTemplate = WhatsAppTemplate.Trim(); s.WhatsAppHeaderName = WhatsAppHeaderName.Trim();
        s.Provider = AssistantProviders.Normalize(Provider);
        var pathChanged = !string.Equals(s.ClaudeCodePath, ClaudeCodePath.Trim(), StringComparison.OrdinalIgnoreCase);
        s.ClaudeCodePath = ClaudeCodePath.Trim(); s.ClaudeCodeModel = ClaudeCodeModel.Trim();
        if (int.TryParse(ClaudeTimeout, out var cto)) s.ClaudeCodeTimeoutSeconds = Math.Clamp(cto, 30, 1800);
        if (pathChanged) _ = CheckClaude();
        try
        {
            s.Save();
            if (ApiKey.Trim().Length > 0)
            {
                if (ApiKey.Trim() == "-") _host.Vault.Set(SecretNames.AnthropicKey, "");
                else _host.Project.Settings.AnthropicApiKey = ApiKey.Trim();   // DPAPI vault on Windows (never settings.json)
                if (!_host.Vault.IsPersistent) _host.Vault.Set(SecretNames.AnthropicKey, ApiKey.Trim() == "-" ? "" : ApiKey.Trim());
            }
            if (!string.IsNullOrWhiteSpace(Model)) { _host.Project.Settings.AnthropicModel = Model.Trim(); _host.Project.Settings.Save(); }
            SetSecret(SecretNames.SmtpPassword, SmtpPassword);
            SetSecret(SecretNames.TeamsWebhook, TeamsUrl);
            SetSecret(SecretNames.WhatsAppWebhook, WhatsAppUrl);
            SetSecret(SecretNames.WhatsAppHeaderValue, WhatsAppHeaderValue);
            SaveRules();
            _host.ApplyModel();
            if (langChanged)
            {
                LocService.Instance.Apply(s.Language);
                _toasts.Show(Loc.T("Dlg_SavedTitle"), Loc.T("Dlg_RestartForFormats"), ToastKind.Good, 8);
            }
            else _toasts.Show(Loc.T("Dlg_SavedTitle"), kind: ToastKind.Good);
            Load();
        }
        catch (Exception ex) { _toasts.Show(Loc.T("Toast_SaveFailed"), ex.Message, ToastKind.Error); }
    }

    private void SaveRules()
    {
        var owner = _host.Data.User;
        var existing = _host.Store.Rules(owner).ToDictionary(r => r.Id);
        var keep = new HashSet<long>();
        _host.Store.Batch(b =>
        {
            foreach (var row in Rules)
            {
                if (row.Id > 0 && existing.TryGetValue(row.Id, out var r))
                {
                    keep.Add(r.Id);
                    r.EventKind = row.EventKind; r.Channel = row.Channel; r.MinSeverity = row.MinSeverity; r.QuietFrom = row.QuietFrom.Trim(); r.QuietTo = row.QuietTo.Trim();
                    r.Address = row.Address.Trim(); r.Enabled = row.Enabled;
                    b.Update(r);
                }
                else b.Insert(new NotificationRule
                {
                    Owner = owner, EventKind = row.EventKind, Channel = row.Channel, MinSeverity = row.MinSeverity, QuietFrom = row.QuietFrom.Trim(), QuietTo = row.QuietTo.Trim(),
                    Address = row.Address.Trim(), Enabled = row.Enabled,
                });
            }
            foreach (var r in existing.Values.Where(r => !keep.Contains(r.Id))) b.Delete(r);
        }, $"Notification rules of {owner} saved ({Rules.Count})");
    }

    /// <summary>[claude-login] Finds Claude Code and checks the login (runs "claude --version" and "claude auth status").</summary>
    [RelayCommand]
    private async Task CheckClaude()
    {
        if (IsCheckingClaude) return;
        IsCheckingClaude = true;
        ClaudeStatusText = "Checking Claude Code...";
        try
        {
            _host.Settings.ClaudeCodePath = ClaudeCodePath.Trim();
            var st = await Task.Run(() => _host.CheckClaudeCode());
            ClaudeStatusText = st.Message + (st.Found ? "\n" + st.Path : "");
            RefreshStatus();
            ClaudeStatusText = st.Message + (st.Found ? "\n" + st.Path : "");
        }
        finally { IsCheckingClaude = false; }
    }

    /// <summary>[claude-login] Shows the Claude Desktop snippet (option B). The app never edits Claude Desktop's config itself.</summary>
    [RelayCommand]
    private void ShowDesktopConfig()
    {
        DesktopSnippet = _host.DesktopSnippet();
        ShowDesktop = !ShowDesktop;
    }

    [RelayCommand]
    private void CopyDesktopConfig()
    {
        if (DesktopSnippet.Length == 0) DesktopSnippet = _host.DesktopSnippet();
        try
        {
            System.Windows.Clipboard.SetText(DesktopSnippet);
            _toasts.Show("Claude Desktop", "Snippet copied. Paste it into claude_desktop_config.json (see the steps below it).", ToastKind.Good, 6);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.ExternalException) { _toasts.Show("Claude Desktop", ex.Message, ToastKind.Warn); }
    }

    [RelayCommand] private void AddRule() => Rules.Add(new NotifyRuleRow { EventKind = NotifyEvents.Brief, Channel = NotifyChannels.Email, MinSeverity = "OK" });
    [RelayCommand] private void RemoveRule() { if (SelectedRule != null) Rules.Remove(SelectedRule); }

    [RelayCommand]
    private async Task Test(string? channel)
    {
        var svc = _notify();
        if (svc is null || string.IsNullOrEmpty(channel)) return;
        Save();
        var address = Rules.FirstOrDefault(r => r.Channel == channel && r.Address.Length > 0)?.Address ?? "";
        var err = await svc.Hub.TestAsync(channel, address);
        TestResult = err.Length == 0 ? $"{channel}: OK" : $"{channel}: {err}";
    }
}
