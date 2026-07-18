using System.Linq;
using System.Windows;
using System.Windows.Controls;
using BankReconciliation.App.ViewModels;
using Microsoft.Win32;

namespace BankReconciliation.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel(
            App.SettingsService,
            App.LoggingService,
            App.ExcelService,
            App.ReconciliationEngine);

        _viewModel.BrowseForFileRequested += OnBrowseForFileRequested;
        _viewModel.SettingsRequested += OnSettingsRequested;
        _viewModel.ErrorRequested += OnErrorRequested;

        DataContext = _viewModel;
        Closing += (_, _) => _viewModel.CancelCommand.Execute(null);
    }

    private string? OnBrowseForFileRequested()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a Bank Reconciliation workbook",
            Filter = "Excel Workbooks (*.xlsx)|*.xlsx|All Files (*.*)|*.*",
            CheckFileExists = true,
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private void OnSettingsRequested(SettingsViewModel settingsViewModel)
    {
        var window = new SettingsWindow(settingsViewModel) { Owner = this };
        settingsViewModel.SettingsSaved += (_, _) => window.Close();
        window.ShowDialog();
    }

    private void OnErrorRequested(string message)
    {
        MessageBox.Show(this, message, "Bank Reconciliation Tool", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void RecentFilesComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: string path } combo)
        {
            _viewModel.OpenRecentFileCommand.Execute(path);
            combo.SelectedItem = null; // acts as a one-shot picker, not a persistent selection
        }
    }

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDroppedExcelFile(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        var path = TryGetDroppedExcelFile(e);
        if (path is not null) _viewModel.OpenRecentFileCommand.Execute(path);
        e.Handled = true;
    }

    /// <summary>Reuses OpenRecentFileCommand for the actual load — it already
    /// does exactly "load this file path" regardless of where the path came
    /// from, so a drop doesn't need its own separate load plumbing.</summary>
    private static string? TryGetDroppedExcelFile(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return null;
        return files.FirstOrDefault(f => f.EndsWith(".xlsx", System.StringComparison.OrdinalIgnoreCase));
    }
}
