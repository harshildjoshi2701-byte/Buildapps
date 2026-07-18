using System.Windows.Controls;

namespace BankReconciliation.App.Views;

/// <summary>Shared filter-button row used above both the Bank and R365 grids.
/// No code-behind logic needed — everything is bound through to whatever
/// DataContext flows down from the parent window (MainViewModel).</summary>
public partial class StatusFilterBar : UserControl
{
    public StatusFilterBar()
    {
        InitializeComponent();
    }
}
