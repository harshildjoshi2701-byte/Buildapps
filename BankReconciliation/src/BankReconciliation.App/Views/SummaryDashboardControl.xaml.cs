using System.Windows.Controls;

namespace BankReconciliation.App.Views;

/// <summary>
/// Pure display control: its DataContext is set (by MainWindow.xaml) to a
/// <see cref="Core.Models.ReconciliationSummary"/> directly — no dedicated
/// view model needed since every value is a straight read-only bound
/// property with no interaction.
/// </summary>
public partial class SummaryDashboardControl : UserControl
{
    public SummaryDashboardControl()
    {
        InitializeComponent();
    }
}
