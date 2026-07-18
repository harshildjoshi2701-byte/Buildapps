namespace BankReconciliation.Core.Models;

/// <summary>
/// Maps the logical fields the engine needs (date, amount, comment target,
/// etc.) to actual 1-based Excel column numbers.
///
/// Why this is configurable rather than hard-coded: real-world exports vary,
/// and this specific workbook has already changed shape multiple times
/// during development. Rather than special-case any one file, the engine
/// treats the *workbook* as the authority on layout and makes every column
/// position configurable from Settings — so the next structural tweak is a
/// Settings edit, not a code change.
///
/// AS OF THIS VERSION: two separate worksheets, each with its own column
/// range starting at A, plus a "Grouping" column on both sheets (see
/// <see cref="Matching.GroupingMatcher"/>). The defaults below are CONFIRMED
/// against a real reconciled output file the user provided (title row 1,
/// an instructional row 2, blank row 3, headers on row 4, data from row 5) —
/// not guesses. The one field that was wrong in practice (Bank Grouping,
/// entered as column 4 "Account Name" instead of column 5 "Grouping") is
/// exactly what caused nearly the entire Bank sheet to collapse into one
/// false "AP ACCOUNT" bucket, since every bank row shares that account name.
/// If you change the workbook's shape again, re-verify every field here —
/// but don't assume it's wrong just because a run looks off; check the
/// actual header row first.
/// </summary>
public sealed class ColumnMapping
{
    // ---- Worksheet selection ------------------------------------------------
    public string BankWorksheetName { get; set; } = "Bank Transactions";
    public string R365WorksheetName { get; set; } = "R365 Transactions";

    // ---- Bank sheet (confirmed against the real workbook: headers on row 4, data from row 5) ----
    public int BankHeaderRow { get; set; } = 4;
    public int BankDataStartRow { get; set; } = 5;
    public int BankFirstColumn { get; set; } = 1;   // A — start of the highlight range
    public int BankDateColumn { get; set; } = 1;    // A — "Date"
    public int BankCreditColumn { get; set; } = 7;  // G — "Credit"
    public int BankDebitColumn { get; set; } = 8;   // H — "Debit"
    public int BankDescriptionColumn { get; set; } = 6; // F — "Transaction Detail", display only

    /// <summary>1-based Excel column number of the Bank sheet's "Grouping"
    /// column (E / 5). Confirmed against the real workbook's header row.
    /// Column 4 ("Account Name") sits immediately before it and holds the
    /// SAME constant value for every row on a single-account statement —
    /// entering 4 here instead of 5 is exactly the bug that made an entire
    /// bank sheet collapse into one false Grouping bucket.</summary>
    public int BankGroupingColumn { get; set; } = 5;

    public int BankCommentColumn { get; set; } = 12; // L — "Notes" (write target)

    /// <summary>Match ID write target. Deliberately placed in a NEW column
    /// past the real workbook's own columns (1-12) rather than reusing one
    /// of them — the real sheet's own column 10/11 are labeled "Match
    /// Status"/"Match ID" for a different purpose, and writing here under
    /// those headers previously stamped a misleading Match ID number onto
    /// rows the Notes column simultaneously called "No Match".</summary>
    public int BankConfidenceColumn { get; set; } = 13; // M
    public int BankDiffColumn { get; set; } = 14; // N — =Credit-Debit helper formula (write target)

    // ---- R365 sheet (confirmed against the real workbook: headers on row 4, data from row 5) ----
    public int R365HeaderRow { get; set; } = 4;
    public int R365DataStartRow { get; set; } = 5;
    public int R365FirstColumn { get; set; } = 1;    // A — start of the highlight range
    public int R365DateColumn { get; set; } = 1;     // A — "Date"

    /// <summary>1-based Excel column number of the R365 sheet's "Grouping"
    /// column (D / 4). Confirmed against the real workbook: 137 distinct
    /// values observed (mostly numeric linking IDs, plus the "Sysco"
    /// keyword bucket handled separately by <see cref="Matching.CombinationMatcher.RunSpecialComboRules"/>).</summary>
    public int R365GroupingColumn { get; set; } = 4; // D

    /// <summary>R365's older "Ref. #" column — a DIFFERENT field from
    /// <see cref="R365GroupingColumn"/>. This is the original per-row
    /// combination-eligibility keyword check (<see cref="GroupKeyword"/>,
    /// "R365" by default), unrelated to the new Grouping column's
    /// cross-sheet linking-ID mechanism. Both are retained side by side per
    /// spec ("retain all previous matching rules").</summary>
    public int R365ReferenceColumn { get; set; } = 3; // C — "Ref. #"
    public int R365DescriptionColumn { get; set; } = 6; // F — "Comment", display only
    public int R365AmountColumn { get; set; } = 7;  // G — signed "Amount"
    public int R365CommentColumn { get; set; } = 10; // J — "Notes" (write target)

    /// <summary>Match ID write target — see <see cref="BankConfidenceColumn"/>
    /// remarks; same reasoning, a fresh column past the real sheet's own
    /// data rather than reusing (or the previous stale default's column 27,
    /// a leftover from before Bank/R365 were split into separate sheets).</summary>
    public int R365ConfidenceColumn { get; set; } = 11; // K
    public int R365DiffColumn { get; set; } = 12; // L — match-group difference-check formula (write target)

    /// <summary>Case-insensitive substring the R365 "Ref. #" column is checked
    /// against to decide grouped-posting (combination-eligible) vs strict
    /// one-to-one (Rule 1 / Rule 2 in the spec). Independent of the new
    /// Grouping column and its keyword-based named rules (e.g. "Sysco").</summary>
    public string GroupKeyword { get; set; } = "R365";

    public ColumnMapping Clone() => (ColumnMapping)MemberwiseClone();
}
