namespace BankReconciliation.Core.Models;

/// <summary>
/// Maps the logical fields the engine needs (date, amount, comment target,
/// etc.) to actual 1-based Excel column numbers.
///
/// Why this is configurable rather than hard-coded: the spec's prose says
/// "Bank Column A = Date", but the actual sample workbook has Account Number
/// in column A and the real transaction date in column C (Credit/Debit are
/// two separate columns, F and G, rather than one signed Amount column).
/// Rather than special-case the sample file, the engine treats the *workbook*
/// as the authority on layout and makes every column position configurable
/// from Settings — so if a future export from a different bank or a
/// different R365 tenant shifts a column, the user fixes it in Settings
/// instead of needing a code change. Defaults below match the shipped sample
/// exactly (see README "Column Mapping").
/// </summary>
public sealed class ColumnMapping
{
    // ---- Worksheet selection ------------------------------------------------
    /// <summary>Empty string = use the first worksheet in the workbook (matches
    /// the sample, which keeps both blocks on a single "Sheet1").</summary>
    public string WorksheetName { get; set; } = string.Empty;

    // ---- Bank block (defaults: A:H data, I comment, J match ID, K diff) ----
    public int BankHeaderRow { get; set; } = 2;
    public int BankDataStartRow { get; set; } = 3;
    public int BankFirstColumn { get; set; } = 1;   // A — start of the highlight range
    public int BankDateColumn { get; set; } = 3;    // C — "Transaction Date"
    public int BankCreditColumn { get; set; } = 6;  // F — "Credit Amount"
    public int BankDebitColumn { get; set; } = 7;   // G — "Debit Amount"
    public int BankDescriptionColumn { get; set; } = 8; // H — "Description" (also used for named combo rules, e.g. "Sysco")
    public int BankCommentColumn { get; set; } = 9; // I — Reconciliation Comment (write target)
    public int BankConfidenceColumn { get; set; } = 10; // J — Match ID (write target)
    public int BankDiffColumn { get; set; } = 11; // K — =Credit-Debit helper formula (write target)

    // ---- R365 block (defaults: N:Y data, Z comment, AA match ID, AB diff) --
    public int R365HeaderRow { get; set; } = 2;
    public int R365DataStartRow { get; set; } = 3;
    public int R365FirstColumn { get; set; } = 14;   // N — start of the highlight range
    public int R365DateColumn { get; set; } = 14;    // N — "Date"
    public int R365ReferenceColumn { get; set; } = 16; // P — "Ref. #" (grouped-posting keyword lives here)
    public int R365DescriptionColumn { get; set; } = 21; // U — "Comment" (display only)
    public int R365AmountColumn { get; set; } = 25;  // Y — signed "Amount"
    public int R365CommentColumn { get; set; } = 26; // Z — Reconciliation Comment (write target)
    public int R365ConfidenceColumn { get; set; } = 27; // AA — Match ID (write target)
    public int R365DiffColumn { get; set; } = 28; // AB — match-group difference-check formula (write target)

    /// <summary>Case-insensitive substring the R365 "Ref. #" column is checked
    /// against to decide grouped-posting (combination-eligible) vs strict
    /// one-to-one (Rule 1 / Rule 2 in the spec).</summary>
    public string GroupKeyword { get; set; } = "R365";

    public ColumnMapping Clone() => (ColumnMapping)MemberwiseClone();
}
