namespace BankReconciliation.Core.Models;

/// <summary>
/// All user-tunable knobs for a reconciliation run. Persisted to JSON by
/// ISettingsService and editable from the Settings window.
/// </summary>
public sealed class ReconciliationSettings
{
    // ---- Matching rules -----------------------------------------------------

    /// <summary>How many days OLDER an R365 transaction may be than the bank
    /// transaction it's being matched against (0 = same day only). Per the
    /// spec, R365 dates are never newer than the bank date.</summary>
    public int MaxDateDifferenceDays { get; set; } = 6;

    /// <summary>Allowed absolute difference, in dollars, between two amounts
    /// for them to be considered "equal" (guards against sub-cent rounding
    /// artifacts in source data; 0.00 means bit-for-bit-in-cents exact).</summary>
    public decimal AmountToleranceDollars { get; set; } = 0.00m;

    /// <summary>Combination matches scoring below this confidence are routed to
    /// Manual Review instead of being auto-matched.</summary>
    public int MinCombinationConfidence { get; set; } = 80;

    /// <summary>Ignore rows that already have reconciliation comment text when
    /// re-running on a workbook that was already (partially) processed. When
    /// true these rows are treated as pre-locked: left untouched, excluded
    /// from every pass, not counted as "unmatched".</summary>
    public bool IgnoreAlreadyReconciledRows { get; set; } = true;

    /// <summary>When true, only R365 rows whose Ref# column contains the
    /// grouped-posting keyword (see <see cref="ColumnMapping.GroupKeyword"/>)
    /// are eligible for Pass 3 combination matching (the original spec's Rule
    /// 1/Rule 2 split). When false (the default), EVERY still-unmatched R365
    /// row is eligible for combination matching regardless of what is in that
    /// column — real-world data often has legitimate combinable postings that
    /// are not tagged, so this is off by default.</summary>
    public bool RequireGroupKeywordForCombinations { get; set; } = false;

    /// <summary>Named, curated keyword-grouping rules, unconditional and
    /// unrestricted by the normal date window. See <see cref="SpecialComboRule"/>
    /// and <see cref="Matching.CombinationMatcher"/> remarks.
    ///
    /// The shipped default rule now targets the new Grouping column on BOTH
    /// sides (column 8 / column 2 — see <see cref="ColumnMapping.BankGroupingColumn"/>
    /// and <see cref="ColumnMapping.R365GroupingColumn"/>), checking for the
    /// literal value "Sysco" on each: the real workbook's Grouping column was
    /// found to hold "Sysco" as a keyword tag that never sum-matches (unlike
    /// every other Grouping value, which is a numeric linking ID that DOES
    /// sum-match). Keeping Sysco on this named-rule mechanism rather than
    /// folding it into <see cref="Matching.GroupingMatcher"/>'s bucket-and-sum
    /// logic is deliberate — see that class's remarks for why running named
    /// rules first makes the split correct with no special-casing required.</summary>
    public List<SpecialComboRule> SpecialComboRules { get; set; } = new()
    {
        new SpecialComboRule { BankColumn = 8, BankKeyword = "Sysco", R365Column = 2, R365Keyword = "Sysco" },
    };

    /// <summary>Confidence score assigned to every match found by a
    /// <see cref="SpecialComboRules"/> rule. These are curated, high-trust
    /// business rules matched purely on keyword presence (no amount, sign,
    /// or date check) — the fixed score reflects that it's a business rule,
    /// not a computed/verified match. Consider lowering this if you want
    /// named-rule matches to stand out from confirmed exact/combination
    /// matches when sorting by confidence.</summary>
    public int SpecialComboConfidenceScore { get; set; } = 90;

    // ---- Combination search performance/safety valves ------------------------
    // Unrestricted subset-sum over an unbounded candidate pool is NP-hard, so
    // *some* bound is required for any engine to guarantee it finishes. These
    // defaults are intentionally generous — the compiled C# engine is far
    // faster than the Python prototype used to validate the algorithm design,
    // and the app runs the combination sweep on a background thread with a
    // visible progress bar and a Cancel button, so a longer worst-case budget
    // is an acceptable trade for finding more real matches.

    /// <summary>If more than this many same-sign, in-date-window R365
    /// candidates exist for one bank transaction, only the N candidates
    /// closest to the bank date are searched, and the transaction is flagged
    /// for Manual Review rather than silently searched on a partial view.</summary>
    public int MaxCombinationPoolSize { get; set; } = 600;

    /// <summary>Hard cap on the number of distinct partial-sum states the
    /// dynamic-programming search may create for a single transaction before
    /// giving up and flagging Manual Review.</summary>
    public int MaxDpStates { get; set; } = 400_000;

    /// <summary>Wall-clock budget, per bank transaction, for the combination
    /// search before it gives up and flags Manual Review.</summary>
    public double PerTransactionTimeBudgetSeconds { get; set; } = 5.0;

    /// <summary>Overall wall-clock budget for the whole Pass 3 sweep (applied
    /// separately to each of the three general sweeps — see
    /// <see cref="Matching.CombinationMatcher"/> remarks). Exists as a
    /// last-resort circuit breaker so a pathological file can never hang the
    /// UI indefinitely; remaining transactions are flagged Manual Review.</summary>
    public double GlobalCombinationTimeBudgetSeconds { get; set; } = 120.0;

    // ---- Threading ------------------------------------------------------------
    public int MaxThreads { get; set; } = Math.Max(1, Environment.ProcessorCount - 1);

    // ---- Output ---------------------------------------------------------------
    public bool AutoSaveOutput { get; set; } = true;

    /// <summary>Writes a unique, sortable Match ID number into the confidence
    /// column (J for Bank, AA for R365) for every matched row — the SAME
    /// number on the bank row and the R365 row(s) it matched with, so sorting
    /// either block by that column groups a match's transactions together.
    /// Unmatched rows are left blank in that column. Also gates the K/AB
    /// helper-formula columns, which depend on the Match ID columns.</summary>
    public bool WriteConfidenceColumn { get; set; } = true;

    public bool HighlightFullRow { get; set; } = true;

    /// <summary>When true (the default), only No Match rows get a highlight
    /// fill color (red) — Matched/Manual Review/Combination rows are left
    /// uncolored for a cleaner sheet where only real problems stand out. Set
    /// to false to restore full green/yellow/blue/red highlighting on every
    /// row.</summary>
    public bool HighlightOnlyUnmatched { get; set; } = true;

    /// <summary>Hides the blank spacer columns between the Bank block and the
    /// R365 block, and autofits every column's width based on the header row
    /// (row 2) content, so the output sheet reads cleanly without manual
    /// tidy-up.</summary>
    public bool TidyColumnsOnOutput { get; set; } = true;

    // ---- Structure --------------------------------------------------------
    public ColumnMapping Columns { get; set; } = new();
    public HighlightColors Colors { get; set; } = new();

    /// <summary>Most-recently-opened workbook paths, newest first, capped at 10.</summary>
    public List<string> RecentFiles { get; set; } = new();

    public bool DarkMode { get; set; }

    public ReconciliationSettings Clone()
    {
        var clone = (ReconciliationSettings)MemberwiseClone();
        clone.Columns = Columns.Clone();
        clone.Colors = Colors.Clone();
        clone.RecentFiles = new List<string>(RecentFiles);
        clone.SpecialComboRules = SpecialComboRules.Select(r => r.Clone()).ToList();
        return clone;
    }
}
