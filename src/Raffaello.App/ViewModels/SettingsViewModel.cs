using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.App.Services;
using Raffaello.Core.Ai;
using Raffaello.Core.Rules;

namespace Raffaello.App.ViewModels;

public sealed record RuleInfo(string Code, string Title, string Description);

public sealed partial class SettingsViewModel : PageViewModel
{
    public SettingsViewModel(PageContext ctx) : base(ctx) { }

    public override string Key => "Settings";
    public override string Title => "SETTINGS";
    public override string Subtitle => "Stored per user in %APPDATA%\\Raffaello\\settings.json";
    public override bool ShowFilterBar => false;
    protected override bool UsesFilter => false;

    public string[] Themes { get; } = { "Light", "Dark" };
    public IEnumerable<string> Accents => ThemeService.Accents.Keys;
    public string[] Efforts { get; } = { "low", "medium", "high", "xhigh", "max" };
    public IEnumerable<RuleInfo> Rules => Project.Engine.Rules.Select(r => new RuleInfo(r.Code, r.Title, r.Description));

    [ObservableProperty] private string _userName = "";
    [ObservableProperty] private string _dataFilePath = "";
    [ObservableProperty] private string _theme = "Light";
    [ObservableProperty] private string _accent = "Red";
    [ObservableProperty] private bool _seedDemoData = true;
    [ObservableProperty] private string _wirDueDays = "14";
    [ObservableProperty] private string _siteTolerance = "15";
    [ObservableProperty] private string _pipeLength = "6";
    [ObservableProperty] private string _apiKey = "";
    [ObservableProperty] private string _model = "";
    [ObservableProperty] private string _effort = "medium";
    [ObservableProperty] private bool _useFallbacks = true;
    [ObservableProperty] private string _keyStatus = "";
    [ObservableProperty] private string _invoiceProjectCode = "";
    [ObservableProperty] private string _invoiceProjectDirector = "";
    [ObservableProperty] private string _invoiceVendorNo = "";
    [ObservableProperty] private string _invoiceSignatureNames = "";
    [ObservableProperty] private string _lengthRoundingDecimals = "1";
    [ObservableProperty] private string _packageOutputFolder = "";
    [ObservableProperty] private string _wirFolder = "";
    [ObservableProperty] private string _packageNamePattern = "";
    [ObservableProperty] private string _trackerPassword = "";

    private bool _loading;
    partial void OnThemeChanged(string value) { if (IsActive && !_loading) Ctx.Theme.Apply(value, Accent); }
    partial void OnAccentChanged(string value) { if (IsActive && !_loading) Ctx.Theme.Apply(Theme, value); }

    protected override void Refresh()
    {
        var s = Project.Settings;
        UserName = s.EffectiveUserName;
        DataFilePath = s.DataFilePath;
        _loading = true;
        Theme = s.Theme;
        Accent = s.Accent;
        _loading = false;
        SeedDemoData = s.SeedDemoData;
        WirDueDays = s.WirDueDays.ToString(CultureInfo.InvariantCulture);
        SiteTolerance = (s.SiteTolerance * 100).ToString("0", CultureInfo.InvariantCulture);
        PipeLength = s.PipeLengthM.ToString("0.##", CultureInfo.InvariantCulture);
        ApiKey = s.AnthropicApiKey;
        Model = s.AnthropicModel;
        Effort = s.AnthropicEffort;
        UseFallbacks = s.UseServerFallbacks;
        InvoiceProjectCode = s.InvoiceProjectCode;
        InvoiceProjectDirector = s.InvoiceProjectDirector;
        InvoiceVendorNo = s.InvoiceVendorNo;
        InvoiceSignatureNames = s.InvoiceSignatureNames;
        LengthRoundingDecimals = s.LengthRoundingDecimals.ToString(CultureInfo.InvariantCulture);
        PackageOutputFolder = s.PackageOutputFolder;
        WirFolder = s.WirFolder;
        PackageNamePattern = s.PackageNamePattern;
        TrackerPassword = s.TrackerPassword;
        KeyStatus = AnthropicClient.ResolveKey(s.AnthropicApiKey) is null ? "NO KEY - Ask Raffaello runs offline (rules engine only)"
            : string.IsNullOrWhiteSpace(s.AnthropicApiKey) ? "USING ANTHROPIC_API_KEY FROM THE ENVIRONMENT" : "KEY SAVED IN SETTINGS";
    }

    [RelayCommand]
    private async Task Save()
    {
        var s = Project.Settings;
        var pathChanged = !string.Equals(s.DataFilePath, DataFilePath, StringComparison.OrdinalIgnoreCase);
        s.UserName = UserName.Trim();
        s.DataFilePath = DataFilePath.Trim();
        s.Theme = Theme;
        s.Accent = Accent;
        s.SeedDemoData = SeedDemoData;
        if (int.TryParse(WirDueDays, out var d)) s.WirDueDays = Math.Clamp(d, 1, 120);
        if (double.TryParse(SiteTolerance, NumberStyles.Float, CultureInfo.InvariantCulture, out var t)) s.SiteTolerance = Math.Clamp(t / 100.0, 0, 1);
        if (double.TryParse(PipeLength, NumberStyles.Float, CultureInfo.InvariantCulture, out var pl) && pl > 0) s.PipeLengthM = pl;
        s.AnthropicApiKey = ApiKey.Trim();
        s.AnthropicModel = string.IsNullOrWhiteSpace(Model) ? AnthropicClient.DefaultModel : Model.Trim();
        s.AnthropicEffort = Effort;
        s.UseServerFallbacks = UseFallbacks;
        s.InvoiceProjectCode = InvoiceProjectCode.Trim();
        s.InvoiceProjectDirector = InvoiceProjectDirector.Trim();
        s.InvoiceVendorNo = InvoiceVendorNo.Trim();
        s.InvoiceSignatureNames = InvoiceSignatureNames.Trim();
        if (int.TryParse(LengthRoundingDecimals, out var lr)) s.LengthRoundingDecimals = Math.Clamp(lr, 0, 3);
        s.PackageOutputFolder = PackageOutputFolder.Trim();
        s.WirFolder = WirFolder.Trim();
        s.PackageNamePattern = string.IsNullOrWhiteSpace(PackageNamePattern) ? Raffaello.Core.Packaging.PackageNames.DefaultPattern : PackageNamePattern.Trim();
        s.TrackerPassword = string.IsNullOrWhiteSpace(TrackerPassword) ? "RAFFAELLO" : TrackerPassword.Trim();
        s.Save();
        Project.CurrentUser = s.EffectiveUserName;
        if (pathChanged)
        {
            await Task.Run(() => Project.Initialize());
            Ctx.Data.RaiseChanged();
            Ctx.Toasts.Show("DATA FILE OPENED", s.DataFilePath, ToastKind.Good);
        }
        else await Ctx.Data.ReloadAsync();
        Ctx.Toasts.Show("SETTINGS SAVED", kind: ToastKind.Good);
    }

    [RelayCommand]
    private void BrowseData()
    {
        var f = Ctx.Dialogs.SaveFile("Choose or create the shared data file", "raffaello.db", "Raffaello data|*.db|All files|*.*");
        if (f != null) DataFilePath = f;
    }

    [RelayCommand]
    private async Task ResetDemo()
    {
        if (!Ctx.Dialogs.Confirm("Reset data", "Delete everything in the data file and rebuild the demo project?\n\nOther users of this file will see the change.")) return;
        await Ctx.Data.WriteAsync(p => p.ResetData(seedDemo: true), Ctx.Toasts, "DEMO DATA REBUILT");
    }

    [RelayCommand]
    private async Task StartEmpty()
    {
        if (!Ctx.Dialogs.Confirm("Start empty", "Delete everything in the data file and start with an empty project?")) return;
        Project.Settings.SeedDemoData = false;
        Project.Settings.Save();
        SeedDemoData = false;
        await Ctx.Data.WriteAsync(p => p.ResetData(seedDemo: false), Ctx.Toasts, "DATA FILE CLEARED");
    }
}
