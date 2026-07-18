using System.IO;
using System.Windows;
using System.Windows.Threading;
using BankReconciliation.Core.Services;

namespace BankReconciliation.App;

/// <summary>
/// Composition root. This app is small enough that a full DI container would
/// be overhead for no real benefit — services are constructed once here and
/// passed down through plain constructor parameters ("poor man's DI"). If the
/// app grows enough to want a container later, every dependency is already
/// expressed as an interface (<see cref="ISettingsService"/>,
/// <see cref="ILoggingService"/>, <see cref="IExcelService"/>,
/// <see cref="Core.Matching.IReconciliationEngine"/>), so swapping in
/// Microsoft.Extensions.DependencyInjection would only touch this file.
/// </summary>
public partial class App : Application
{
    public static ISettingsService SettingsService { get; private set; } = null!;
    public static ILoggingService LoggingService { get; private set; } = null!;
    public static IExcelService ExcelService { get; private set; } = null!;
    public static Core.Matching.IReconciliationEngine ReconciliationEngine { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Any unhandled exception anywhere in the app is logged and shown to
        // the user in a plain-language dialog instead of a silent crash —
        // finance users running an overnight batch reconciliation need to
        // know if something went wrong, not just see the window disappear.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

        SettingsService = new SettingsService();
        LoggingService = new LoggingService();
        ExcelService = new ExcelService();
        ReconciliationEngine = new Core.Matching.ReconciliationEngine();

        var settings = SettingsService.Load();
        ApplyTheme(settings.DarkMode);
    }

    /// <summary>Swaps the first merged dictionary (the theme palette) for
    /// Light.xaml or Dark.xaml. Every control in the app reads colors via
    /// DynamicResource, so this takes effect immediately on every open
    /// window — no restart required.</summary>
    public static void ApplyTheme(bool darkMode)
    {
        var dictionaries = Current.Resources.MergedDictionaries;
        var themeSource = darkMode ? "Themes/Dark.xaml" : "Themes/Light.xaml";
        var newTheme = new ResourceDictionary { Source = new Uri(themeSource, UriKind.Relative) };

        if (dictionaries.Count > 0)
            dictionaries[0] = newTheme;
        else
            dictionaries.Add(newTheme);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogAndShow(e.Exception);
        e.Handled = true; // keep the app alive — a single failed operation shouldn't take down the whole session
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex) LogAndShow(ex);
    }

    private static void LogAndShow(Exception ex)
    {
        try
        {
            var crashLogPath = Path.Combine(LoggingService.LogDirectory, $"crash_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            File.WriteAllText(crashLogPath, ex.ToString());
        }
        catch
        {
            // If we can't even write the crash log, fall through to the
            // dialog anyway — the user still deserves to know something broke.
        }

        MessageBox.Show(
            $"Something went wrong:\n\n{ex.Message}\n\nDetails were written to the log folder.",
            "Bank Reconciliation Tool — Unexpected Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
