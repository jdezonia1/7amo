using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Raffaello.App.Services;
using Raffaello.App.ViewModels;
using Raffaello.App.Views;
using Raffaello.Core;
using Raffaello.Core.Queue;
using Raffaello.Core.Settings;

namespace Raffaello.App;

/// <summary>Lets page view models navigate before the shell exists (breaks the DI cycle shell -> pages -> navigator).</summary>
public sealed class NavigatorProxy : INavigator
{
    public INavigator? Target { get; set; }
    public void Go(string key, NavTarget? target = null) => Target?.Go(key, target);
    public void OpenImport(string? kind = null) => Target?.OpenImport(kind);
    public void OpenAsk(string? question = null) => Target?.OpenAsk(question);
}

public partial class App : Application
{
    private IHost? _host;

    public static string ErrorLogPath => Path.Combine(AppSettings.SettingsFolder, "error.log");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        try
        {
            await StartAsync();
        }
        catch (Exception ex)
        {
            Log(ex);
            MessageBox.Show($"Raffaello could not start.\n\n{ex.Message}\n\nDetails: {ErrorLogPath}", "Raffaello", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task StartAsync()
    {

        var settings = AppSettings.Load();
        var theme = new ThemeService();
        theme.Apply(settings.Theme, settings.Accent);

        var splash = new SplashWindow();
        splash.Show();

        // [phase5] begin: data source = local SQLite file or Raffaello server (Settings > Data source)
        var project = new ProjectService(settings, Raffaello.Core.Remote.DataSourceFactory.Create);
        // [phase5] end
        try
        {
            await Task.Run(() => project.Initialize(msg => splash.Dispatcher.Invoke(() => splash.Status = msg)));
        }
        catch (Exception ex)
        {
            Log(ex);
            var fallback = AppSettings.DefaultDataPath();
            MessageBox.Show($"Could not open the data file:\n{settings.DataFilePath}\n\n{ex.Message}\n\nOpening the local file instead:\n{fallback}",
                "Raffaello", MessageBoxButton.OK, MessageBoxImage.Warning);
            settings.DataFilePath = fallback;
            project = new ProjectService(settings);
            await Task.Run(() => project.Initialize(msg => splash.Dispatcher.Invoke(() => splash.Status = msg)));
        }

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(s =>
            {
                s.AddSingleton(settings);
                s.AddSingleton(theme);
                s.AddSingleton(project);
                s.AddSingleton<DataService>();
                s.AddSingleton<FilterState>();
                s.AddSingleton<ToastService>();
                s.AddSingleton<DialogService>();
                s.AddSingleton<ExportService>();
                s.AddSingleton<SelectionService>();
                s.AddSingleton<PresenceService>();
                s.AddSingleton<NavigatorProxy>();
                s.AddSingleton(sp => new PageContext
                {
                    Nav = sp.GetRequiredService<NavigatorProxy>(),
                    Data = sp.GetRequiredService<DataService>(),
                    Filter = sp.GetRequiredService<FilterState>(),
                    Toasts = sp.GetRequiredService<ToastService>(),
                    Dialogs = sp.GetRequiredService<DialogService>(),
                    Exports = sp.GetRequiredService<ExportService>(),
                    Selection = sp.GetRequiredService<SelectionService>(),
                    Theme = sp.GetRequiredService<ThemeService>(),
                });
                s.AddSingleton<PageViewModel, WelcomeViewModel>();
                s.AddSingleton<PageViewModel, DashboardViewModel>();
                s.AddSingleton<PageViewModel, LedgerViewModel>();
                s.AddSingleton<PageViewModel, ChecksViewModel>();
                s.AddSingleton<PageViewModel, SiteStatementsViewModel>();
                s.AddSingleton<PageViewModel, QuantitiesViewModel>();
                s.AddSingleton<PageViewModel, PlanViewModel>();
                s.AddSingleton<PageViewModel, StatementsViewModel>();
                // [phase4] begin - the ACONEX item is now the hub; the script runner page lives inside it as a tab
                s.AddSingleton<AconexAutomationService>();
                s.AddSingleton<AconexViewModel>();
                s.AddSingleton<PageViewModel, AconexHubViewModel>();
                s.AddSingleton<PageViewModel, VariationsViewModel>();
                // [phase4] end
                // [phase3] begin - the Materials nav item is the phase-3 hub; the earlier PO vs DN screen is its OVERVIEW tab
                s.AddSingleton(_ => Raffaello.Core.Materials.MaterialsSettings.Load());
                // [phase6] SQLite data file or the server, chosen per call by the current data source
                s.AddSingleton<Raffaello.Core.Materials.IMaterialsStore>(sp => new Raffaello.Core.Remote.MaterialsStoreSelector(() => sp.GetRequiredService<ProjectService>().Store));
                s.AddSingleton<Raffaello.Core.Materials.MaterialsService>();
                s.AddSingleton<Raffaello.Core.Documents.IOcrEngine, WindowsOcrEngine>();
                s.AddSingleton<Raffaello.Core.Documents.IPageRenderer, WindowsPdfRenderer>();
                s.AddSingleton<MaterialsViewModel>();
                s.AddSingleton<PageViewModel, MaterialsHubViewModel>();
                s.AddSingleton<PageViewModel, OwnerMosViewModel>();
                s.AddSingleton<PageViewModel, BoqViewModel>();
                // [phase3] end
                // [insights] begin - anomalies, material reconciliation, rate benchmark, cash flow, earned value
                s.AddSingleton<Raffaello.Core.Insights.IInsightsStore>(sp => new Raffaello.Core.Insights.InsightsStoreSelector(() => sp.GetRequiredService<ProjectService>().Store));
                s.AddSingleton<ViewModels.Insights.InsightsHub>();
                s.AddSingleton<ViewModels.Insights.InsightsReportsPanel>();
                s.AddSingleton<PageViewModel, ViewModels.Insights.InsightsAnomaliesViewModel>();
                s.AddSingleton<PageViewModel, ViewModels.Insights.InsightsReconViewModel>();
                s.AddSingleton<PageViewModel, ViewModels.Insights.InsightsRatesViewModel>();
                s.AddSingleton<PageViewModel, ViewModels.Insights.InsightsCashFlowViewModel>();
                s.AddSingleton<PageViewModel, ViewModels.Insights.InsightsEarnedValueViewModel>();
                // [insights] end
                s.AddSingleton<PageViewModel, WirViewModel>();
                s.AddSingleton<PageViewModel, ContractsViewModel>();
                s.AddSingleton<PageViewModel, InvoicesViewModel>();
                s.AddSingleton<PageViewModel, ReportsViewModel>();
                s.AddSingleton<PageViewModel, SettingsViewModel>();
                s.AddSingleton<AskViewModel>();
                s.AddSingleton<CommandPaletteViewModel>();
                s.AddSingleton<ImportViewModel>();
                s.AddSingleton<MainViewModel>();
                s.AddSingleton<MainWindow>();
                s.AddTransient<FirstRunViewModel>();   // [phase6]
            })
            .Build();

        var sp = _host.Services;
        // [phase6] "Needs you today" from every module (each source is skipped when its data cannot be read)
        {
            var mats = sp.GetRequiredService<Raffaello.Core.Materials.IMaterialsStore>();
            var matSettings = sp.GetRequiredService<Raffaello.Core.Materials.MaterialsSettings>();
            var aconex = sp.GetRequiredService<AconexAutomationService>();
            project.QueueSources.Add(p => Raffaello.Core.Queue.ModuleQueue.Materials(mats.Load(), matSettings, DateTime.Today));
            project.QueueSources.Add(p => Raffaello.Core.Queue.ModuleQueue.Aconex(Raffaello.Core.AconexWeb.StatusBoard.Build(p.Snapshot.SubInvoices, aconex.Store.ActiveLinks(), aconex.Store.LatestChecks(), DateTime.Today, false)));
            project.QueueSources.Add(p => Raffaello.Core.Queue.ModuleQueue.Variations(aconex.Variations.Variations(), DateTime.Today));
            // [insights] begin: warnings feed "Needs you today"; every subcontractor invoice package gets the insight-check page
            var insights = sp.GetRequiredService<ViewModels.Insights.InsightsHub>();
            Raffaello.Core.Insights.RemoteInsightsStore.Register();
            project.QueueSources.Add(p => insights.QueueItems(p));
            Raffaello.Core.Packaging.InvoicePackageBuilder.GlobalSections.Add(Raffaello.Core.Insights.InsightsPackage.Section(insights.LoadData));
            // [insights] end
            try { project.Reload(); } catch (Exception ex) { Log(ex); }
        }
        var main = sp.GetRequiredService<MainViewModel>();
        sp.GetRequiredService<NavigatorProxy>().Target = main;
        sp.GetRequiredService<FilterState>().RebuildOptions();
        main.UpdateBadges();

        // [phase6] first-run wizard (data source, documents folder, first imports, Aconex, demo mode)
        if (!settings.FirstRunCompleted)
        {
            splash.Hide();
            var wizard = new FirstRunWindow(sp.GetRequiredService<FirstRunViewModel>());
            wizard.ShowDialog();
            if (wizard.DataContext is FirstRunViewModel { NeedsRestart: true })
                MessageBox.Show("The data source changed. Raffaello will use it from the next start - close and open the app again.", "Raffaello", MessageBoxButton.OK, MessageBoxImage.Information);
            sp.GetRequiredService<DataService>().RaiseChanged();
        }

        var window = sp.GetRequiredService<MainWindow>();
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
        splash.Close();
        main.Go("Welcome");
        sp.GetRequiredService<PresenceService>().Start();
        // [phase5] begin: live "updated by X" toasts, offline / sync status
        new Services.Phase5.RemoteSyncService(sp.GetRequiredService<DataService>(), sp.GetRequiredService<ToastService>()).Start(project.Store as Raffaello.Core.Remote.RemoteProjectStore);
        // [phase5] end
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        var toasts = _host?.Services.GetService<ToastService>();
        if (toasts != null) toasts.Show("SOMETHING WENT WRONG", e.Exception.Message + " (details in error.log)", ToastKind.Error, 8);
        else MessageBox.Show(e.Exception.ToString(), "Raffaello", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    public static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.SettingsFolder);
            File.AppendAllText(ErrorLogPath, $"[{DateTime.Now:s}] {ex}\n\n");
        }
        catch { /* logging must never throw */ }
    }
}
