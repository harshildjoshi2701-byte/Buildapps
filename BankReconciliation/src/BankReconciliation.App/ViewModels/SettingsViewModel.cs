using System.Collections.ObjectModel;
using System.Linq;
using BankReconciliation.Core.Models;
using BankReconciliation.Core.Services;

namespace BankReconciliation.App.ViewModels;

/// <summary>
/// Backs the Settings window. Edits happen on a CLONE of the live settings
/// (<see cref="ReconciliationSettings.Clone"/>) so a Cancel truly discards
/// every change; Save persists the clone back through
/// <see cref="ISettingsService"/> and becomes the new live settings.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settingsService;
    private readonly ReconciliationSettings _working;

    public SettingsViewModel(ISettingsService settingsService, ReconciliationSettings current)
    {
        _settingsService = settingsService;
        _working = current.Clone();

        SaveCommand = new RelayCommand(Save);
        ResetDefaultsCommand = new RelayCommand(ResetToDefaults);
        AddSpecialComboRuleCommand = new RelayCommand(AddSpecialComboRule);

        SpecialComboRules = new ObservableCollection<SpecialComboRuleViewModel>(
            _working.SpecialComboRules.Select(WrapRule));
    }

    /// <summary>Set by the view after a successful Save; the window closes when this changes.</summary>
    public event EventHandler? SettingsSaved;

    public RelayCommand SaveCommand { get; }
    public RelayCommand ResetDefaultsCommand { get; }
    public RelayCommand AddSpecialComboRuleCommand { get; }

    /// <summary>Backs the "Custom Matching Rules" list in Settings — each
    /// entry is a user-editable, unconditional keyword-pairing rule that
    /// runs BEFORE Pass 1 (see <see cref="Matching.ReconciliationEngine"/>).
    /// Ships with one default rule (Sysco/Online, columns 8/16) but is fully
    /// add/remove/edit-able from the UI.</summary>
    public ObservableCollection<SpecialComboRuleViewModel> SpecialComboRules { get; }

    private SpecialComboRuleViewModel WrapRule(SpecialComboRule rule) =>
        new(rule, removed => SpecialComboRules.Remove(removed));

    private void AddSpecialComboRule()
    {
        var rule = new SpecialComboRule { BankColumn = 8, BankKeyword = string.Empty, R365Column = 2, R365Keyword = string.Empty };
        SpecialComboRules.Add(WrapRule(rule));
    }

    public ReconciliationSettings Result => _working;

    // ---- Matching rules ----
    public int MaxDateDifferenceDays
    {
        get => _working.MaxDateDifferenceDays;
        set { _working.MaxDateDifferenceDays = Math.Clamp(value, 0, 365); OnPropertyChanged(); }
    }

    public decimal AmountToleranceDollars
    {
        get => _working.AmountToleranceDollars;
        set { _working.AmountToleranceDollars = Math.Max(0, value); OnPropertyChanged(); }
    }

    public int MinCombinationConfidence
    {
        get => _working.MinCombinationConfidence;
        set { _working.MinCombinationConfidence = Math.Clamp(value, 0, 100); OnPropertyChanged(); }
    }

    public bool IgnoreAlreadyReconciledRows
    {
        get => _working.IgnoreAlreadyReconciledRows;
        set { _working.IgnoreAlreadyReconciledRows = value; OnPropertyChanged(); }
    }

    public bool RequireGroupKeywordForCombinations
    {
        get => _working.RequireGroupKeywordForCombinations;
        set { _working.RequireGroupKeywordForCombinations = value; OnPropertyChanged(); }
    }

    // ---- Performance / safety valves ----
    public int MaxCombinationPoolSize
    {
        get => _working.MaxCombinationPoolSize;
        set { _working.MaxCombinationPoolSize = Math.Max(10, value); OnPropertyChanged(); }
    }

    public int MaxThreads
    {
        get => _working.MaxThreads;
        set { _working.MaxThreads = Math.Clamp(value, 1, Environment.ProcessorCount * 2); OnPropertyChanged(); }
    }

    public double GlobalCombinationTimeBudgetSeconds
    {
        get => _working.GlobalCombinationTimeBudgetSeconds;
        set { _working.GlobalCombinationTimeBudgetSeconds = Math.Max(1, value); OnPropertyChanged(); }
    }

    public int MaxThreadsCeiling => Environment.ProcessorCount * 2;

    // ---- Output ----
    public bool AutoSaveOutput
    {
        get => _working.AutoSaveOutput;
        set { _working.AutoSaveOutput = value; OnPropertyChanged(); }
    }

    public bool WriteConfidenceColumn
    {
        get => _working.WriteConfidenceColumn;
        set { _working.WriteConfidenceColumn = value; OnPropertyChanged(); }
    }

    public bool HighlightFullRow
    {
        get => _working.HighlightFullRow;
        set { _working.HighlightFullRow = value; OnPropertyChanged(); }
    }

    public bool HighlightOnlyUnmatched
    {
        get => _working.HighlightOnlyUnmatched;
        set { _working.HighlightOnlyUnmatched = value; OnPropertyChanged(); }
    }

    public bool TidyColumnsOnOutput
    {
        get => _working.TidyColumnsOnOutput;
        set { _working.TidyColumnsOnOutput = value; OnPropertyChanged(); }
    }

    // ---- Colors (hex strings, editable as text for simplicity — a full
    // color-picker control was judged not worth the extra dependency for a
    // v1 given every value is also just a hex string an accountant can type
    // straight from a company style guide if needed) ----
    public string MatchedColorHex
    {
        get => _working.Colors.MatchedHex;
        set { _working.Colors.MatchedHex = value; OnPropertyChanged(); }
    }

    public string ManualReviewColorHex
    {
        get => _working.Colors.ManualReviewHex;
        set { _working.Colors.ManualReviewHex = value; OnPropertyChanged(); }
    }

    public string NoMatchColorHex
    {
        get => _working.Colors.NoMatchHex;
        set { _working.Colors.NoMatchHex = value; OnPropertyChanged(); }
    }

    // ---- Worksheet names (advanced) ----
    public string BankWorksheetName { get => _working.Columns.BankWorksheetName; set { _working.Columns.BankWorksheetName = value; OnPropertyChanged(); } }
    public string R365WorksheetName { get => _working.Columns.R365WorksheetName; set { _working.Columns.R365WorksheetName = value; OnPropertyChanged(); } }

    // ---- Column mapping (advanced) ----
    public int BankDateColumn { get => _working.Columns.BankDateColumn; set { _working.Columns.BankDateColumn = value; OnPropertyChanged(); } }
    public int BankCreditColumn { get => _working.Columns.BankCreditColumn; set { _working.Columns.BankCreditColumn = value; OnPropertyChanged(); } }
    public int BankDebitColumn { get => _working.Columns.BankDebitColumn; set { _working.Columns.BankDebitColumn = value; OnPropertyChanged(); } }
    public int BankGroupingColumn { get => _working.Columns.BankGroupingColumn; set { _working.Columns.BankGroupingColumn = value; OnPropertyChanged(); } }
    public int BankCommentColumn { get => _working.Columns.BankCommentColumn; set { _working.Columns.BankCommentColumn = value; OnPropertyChanged(); } }
    public int R365DateColumn { get => _working.Columns.R365DateColumn; set { _working.Columns.R365DateColumn = value; OnPropertyChanged(); } }
    public int R365GroupingColumn { get => _working.Columns.R365GroupingColumn; set { _working.Columns.R365GroupingColumn = value; OnPropertyChanged(); } }
    public int R365ReferenceColumn { get => _working.Columns.R365ReferenceColumn; set { _working.Columns.R365ReferenceColumn = value; OnPropertyChanged(); } }
    public int R365AmountColumn { get => _working.Columns.R365AmountColumn; set { _working.Columns.R365AmountColumn = value; OnPropertyChanged(); } }
    public int R365CommentColumn { get => _working.Columns.R365CommentColumn; set { _working.Columns.R365CommentColumn = value; OnPropertyChanged(); } }
    public string GroupKeyword { get => _working.Columns.GroupKeyword; set { _working.Columns.GroupKeyword = value; OnPropertyChanged(); } }

    private void Save()
    {
        // Rules may have been added/removed/edited via the UI since load —
        // re-collect the current wrapped list (each VM already writes
        // through to its own rule instance, so this is just picking up
        // adds/removes, not re-copying field values).
        _working.SpecialComboRules = SpecialComboRules.Select(vm => vm.ToModel()).ToList();
        _settingsService.Save(_working);
        SettingsSaved?.Invoke(this, EventArgs.Empty);
    }

    private void ResetToDefaults()
    {
        var defaults = new ReconciliationSettings();
        MaxDateDifferenceDays = defaults.MaxDateDifferenceDays;
        AmountToleranceDollars = defaults.AmountToleranceDollars;
        MinCombinationConfidence = defaults.MinCombinationConfidence;
        IgnoreAlreadyReconciledRows = defaults.IgnoreAlreadyReconciledRows;
        RequireGroupKeywordForCombinations = defaults.RequireGroupKeywordForCombinations;
        MaxCombinationPoolSize = defaults.MaxCombinationPoolSize;
        MaxThreads = defaults.MaxThreads;
        GlobalCombinationTimeBudgetSeconds = defaults.GlobalCombinationTimeBudgetSeconds;
        AutoSaveOutput = defaults.AutoSaveOutput;
        WriteConfidenceColumn = defaults.WriteConfidenceColumn;
        HighlightFullRow = defaults.HighlightFullRow;
        HighlightOnlyUnmatched = defaults.HighlightOnlyUnmatched;
        TidyColumnsOnOutput = defaults.TidyColumnsOnOutput;
        MatchedColorHex = defaults.Colors.MatchedHex;
        ManualReviewColorHex = defaults.Colors.ManualReviewHex;
        NoMatchColorHex = defaults.Colors.NoMatchHex;

        SpecialComboRules.Clear();
        foreach (var rule in defaults.SpecialComboRules)
            SpecialComboRules.Add(WrapRule(rule));
    }
}
