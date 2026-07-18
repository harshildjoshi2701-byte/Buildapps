using BankReconciliation.Core.Models;

namespace BankReconciliation.App.ViewModels;

/// <summary>
/// Binds one row of the "Custom Matching Rules" list in Settings to the
/// underlying <see cref="SpecialComboRule"/> it wraps. Property setters
/// write straight through to the wrapped rule, so the rule is always
/// current — <see cref="SettingsViewModel.Save"/> just re-collects the
/// current list of wrapped rules, no separate sync step needed. Removal is
/// a callback supplied by the owning <see cref="SettingsViewModel"/> rather
/// than a relative-source XAML binding, which is more robust in a
/// hand-verified (not compiler-checked) codebase.
/// </summary>
public sealed class SpecialComboRuleViewModel : ViewModelBase
{
    private readonly SpecialComboRule _rule;

    public SpecialComboRuleViewModel(SpecialComboRule rule, Action<SpecialComboRuleViewModel> onRemove)
    {
        _rule = rule;
        RemoveCommand = new RelayCommand(() => onRemove(this));
    }

    public RelayCommand RemoveCommand { get; }

    public int BankColumn
    {
        get => _rule.BankColumn;
        set { _rule.BankColumn = value; OnPropertyChanged(); }
    }

    public string BankKeyword
    {
        get => _rule.BankKeyword;
        set { _rule.BankKeyword = value; OnPropertyChanged(); }
    }

    public int R365Column
    {
        get => _rule.R365Column;
        set { _rule.R365Column = value; OnPropertyChanged(); }
    }

    public string R365Keyword
    {
        get => _rule.R365Keyword;
        set { _rule.R365Keyword = value; OnPropertyChanged(); }
    }

    /// <summary>Returns the live, already-up-to-date wrapped rule.</summary>
    public SpecialComboRule ToModel() => _rule;
}
