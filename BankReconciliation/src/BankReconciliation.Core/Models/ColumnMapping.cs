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
/// AS OF THIS VERSION: the workbook moved from one worksheet with Bank and
/// R365 blocks side by side (columns A:K and N:AB) to two separate
/// worksheets, each with its own column range starting at A, plus a new
/// "Grouping" column on both sheets (see <see cref="Matching.GroupingMatcher"/>).
/// The defaults below are BEST-EFFORT placeholders inferred from a prior
/// analysis of the real file's shape (12 Bank columns, 10 R365 columns;
/// Grouping reported to occupy the position Description used to hold on the
/// Bank sheet) — NOT confirmed against an actual header row. Verify/correct
/// every column number here against the real workbook before relying on a
/// run's results.
/// </summary>
public sealed class ColumnMapping
{
    // ---- Worksheet selection ------------------------------------------------
    public string BankWorksheetName { get; set; } = "Bank Transactions";
    public string R365WorksheetName { get; set; } = "R365 Transactions";

    // ---- Bank sheet (placeholder defaults: A:K data, comment/matchID/diff write targets) ----
    public int BankHeaderRow { get; set; } = 2;
    public int BankDataStartRow { get; set; } = 3;
    public int BankFirstColumn { get; set; } = 1;   // A — start of the highlight range
    public int BankDateColumn { get; set; } = 3;    // C — "Transaction Date"
    public int BankCreditColumn { get; set; } = 6;  // F — "Credit Amount"
    public int BankDebitColumn { get; set; } = 7;   // G — "Debit Amount"
    public int BankDescriptionColumn { get; set; } = 8; // H — display only

    /// <summary>1-based Excel column number of the Bank sheet's "Grouping"
    /// column. Placeholder default reuses column 8 (H) — the prior analysis
    /// reported Grouping occupying the position the old Description column
    /// held before the sheet split, so this is a reasonable starting guess,
    /// not a confirmed position.</summary>
    public int BankGroupingColumn { get; set; } = 8;

    public int BankCommentColumn { get; set; } = 9; // I — Reconciliation Comment (write target)
    public int BankConfidenceColumn { get; set; } = 10; // J — Match ID (write target)
    public int BankDiffColumn { get; set; } = 11; // K — =Credit-Debit helper formula (write target)

    // ---- R365 sheet (placeholder defaults: A:H data, compacted to its own sheet) ----
    public int R365HeaderRow { get; set; } = 2;
    public int R365DataStartRow { get; set; } = 3;
    public int R365FirstColumn { get; set; } = 1;    // A — start of the highlight range
    public int R365DateColumn { get; set; } = 1;     // A — "Date"

    /// <summary>1-based Excel column number of the R365 sheet's "Grouping"
    /// column (reported to occupy the position a "Location #" column used to
    /// hold). Placeholder default — verify against the real header row.</summary>
    public int R365GroupingColumn { get; set; } = 2; // B

    /// <summary>R365's older "Ref. #" column — a DIFFERENT field from
    /// <see cref="R365GroupingColumn"/>. This is the original per-row
    /// combination-eligibility keyword check (<see cref="GroupKeyword"/>,
    /// "R365" by default), unrelated to the new Grouping column's
    /// cross-sheet linking-ID mechanism. Both are retained side by side per
    /// spec ("retain all previous matching rules").</summary>
    public int R365ReferenceColumn { get; set; } = 3; // C — "Ref. #"
    public int R365DescriptionColumn { get; set; } = 4; // D — display only
    public int R365AmountColumn { get; set; } = 5;  // E — signed "Amount"
    public int R365CommentColumn { get; set; } = 6; // F — Reconciliation Comment (write target)
    public int R365ConfidenceColumn { get; set; } = 7; // G — Match ID (write target)
    public int R365DiffColumn { get; set; } = 8; // H — match-group difference-check formula (write target)

    /// <summary>Case-insensitive substring the R365 "Ref. #" column is checked
    /// against to decide grouped-posting (combination-eligible) vs strict
    /// one-to-one (Rule 1 / Rule 2 in the spec). Independent of the new
    /// Grouping column and its keyword-based named rules (e.g. "Sysco").</summary>
    public string GroupKeyword { get; set; } = "R365";

    public ColumnMapping Clone() => (ColumnMapping)MemberwiseClone();
}
