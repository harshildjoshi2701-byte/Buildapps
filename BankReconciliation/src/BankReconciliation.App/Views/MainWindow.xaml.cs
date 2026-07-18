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
}
