using System.Windows;
using BankReconciliation.App.ViewModels;

namespace BankReconciliation.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
}
