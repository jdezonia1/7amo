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

        var project = new ProjectService(settings);
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
                s.AddSingleton<PageViewModel, StatementsViewModel>();
                s.AddSingleton<PageViewModel, MaterialsViewModel>();
                s.AddSingleton<PageViewModel, AconexViewModel>();
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
            })
            .Build();

        var sp = _host.Services;
        var main = sp.GetRequiredService<MainViewModel>();
        sp.GetRequiredService<NavigatorProxy>().Target = main;
        sp.GetRequiredService<FilterState>().RebuildOptions();
        main.UpdateBadges();

        var window = sp.GetRequiredService<MainWindow>();
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
        splash.Close();
        main.Go("Welcome");
        sp.GetRequiredService<PresenceService>().Start();
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
