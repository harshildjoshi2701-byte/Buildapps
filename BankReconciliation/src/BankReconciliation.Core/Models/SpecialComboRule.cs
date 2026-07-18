namespace BankReconciliation.Core.Models;

/// <summary>
/// A named, user-editable matching rule: every bank row whose
/// <see cref="BankColumn"/> contains <see cref="BankKeyword"/> is grouped
/// together with every R365 row whose <see cref="R365Column"/> contains
/// <see cref="R365Keyword"/> — unconditionally, with no amount, sign, or
/// date check of any kind (see <see cref="Matching.CombinationMatcher"/>
/// remarks). Both keyword checks are case-insensitive substring matches,
/// same convention as <see cref="ColumnMapping.GroupKeyword"/>. Column
/// numbers are independent per rule and per side — they do not need to match
/// <see cref="ColumnMapping.BankDescriptionColumn"/> or
/// <see cref="ColumnMapping.R365ReferenceColumn"/>, which is what lets
/// multiple rules each look at whichever column is relevant to that specific
/// business pattern. Runs as its own pipeline stage BEFORE Pass 1 (see
/// <see cref="Matching.ReconciliationEngine"/>) — named rules are curated,
/// asserted business knowledge, so they get first pick of the transaction
/// pool ahead of anything the algorithm would otherwise guess.
///
/// The default list ships with one rule (Sysco/Online, columns 8/16 to match
/// the shipped template), but this is a plain, UI-editable list — add,
/// remove, or edit rules from the Settings window if you have other known
/// bank/R365 pairings that should always be grouped together regardless of
/// amount or date.
/// </summary>
public sealed class SpecialComboRule
{
    /// <summary>1-based Excel column number on the Bank side this rule
    /// checks. Defaults to 8 (H) to match the shipped template's Description
    /// column, but is independent of <see cref="ColumnMapping.BankDescriptionColumn"/>
    /// — change it per rule as needed.</summary>
    public int BankColumn { get; set; } = 8;

    public string BankKeyword { get; set; } = string.Empty;

    /// <summary>1-based Excel column number on the R365 side this rule
    /// checks. Defaults to 16 (P) to match the shipped template's Ref#
    /// column, but is independent of <see cref="ColumnMapping.R365ReferenceColumn"/>
    /// — change it per rule as needed.</summary>
    public int R365Column { get; set; } = 16;

    public string R365Keyword { get; set; } = string.Empty;

    public SpecialComboRule Clone() => new()
    {
        BankColumn = BankColumn,
        BankKeyword = BankKeyword,
        R365Column = R365Column,
        R365Keyword = R365Keyword,
    };
}
