namespace BankReconciliation.Core.Models;

/// <summary>
/// A single transaction row from either the Bank block or the R365 block of
/// the workbook, normalized into a common shape the matching engine can
/// reason about regardless of which side it came from.
///
/// Amounts are stored as signed cents (<see cref="AmountCents"/>) rather than
/// decimal dollars. Every comparison inside the matching engine is done in
/// integer cents — this avoids the classic floating-point-equality bugs that
/// would otherwise cause a real dollar-for-dollar exact match to be missed by
/// a fraction of a cent. See <see cref="Matching.MoneyMath"/>.
/// </summary>
public sealed class TransactionRecord
{
    /// <summary>1-based Excel row number this record came from — used to write
    /// results back to the exact same row, and as a stable tie-break key.</summary>
    public required int RowNumber { get; init; }

    public required TransactionSide Side { get; init; }

    public required DateTime Date { get; init; }

    /// <summary>Signed amount in cents. Positive = credit, negative = debit.
    /// For Bank rows this is (CreditAmount - DebitAmount). For R365 rows this
    /// is read directly from the signed Amount column.</summary>
    public required long AmountCents { get; init; }

    /// <summary>Raw text of the R365 "Ref. #" column (Column P by default).
    /// Empty for Bank-side records — the grouped-posting rule only ever reads
    /// this column on the R365 side (Rule 1 in the spec).</summary>
    public string Reference { get; init; } = string.Empty;

    /// <summary>True if <see cref="Reference"/> contains the configured group
    /// keyword ("R365" by default), case-insensitive substring match. Only
    /// R365-side records with this flag set are eligible for combination
    /// (subset-sum) matching — see Rule 1 vs Rule 2 in the spec.</summary>
    public bool IsGroupedPosting { get; init; }

    /// <summary>Raw text of the workbook's "Grouping" column — present on
    /// BOTH sides now (unlike <see cref="Reference"/>, which is R365-only).
    /// Empty/blank means "no grouping asserted, use normal matching against
    /// every other ungrouped row." A non-blank value partitions this row into
    /// a private candidate pool shared only with other rows carrying the same
    /// value (case-insensitive) — see <see cref="Matching.GroupingPartitioner"/>.
    /// This covers both a true linking ID (a handful of rows on each side
    /// that sum together) and a vendor/category tag like "Sysco" (many rows
    /// on one side, few on the other, most of which have a genuine individual
    /// match once the search is scoped to just that tag) — partitioning first
    /// and then running the normal matchers handles both without needing to
    /// tell them apart.</summary>
    public string GroupingKey { get; init; } = string.Empty;

    /// <summary>Short human-readable snippet (description/remark) shown in the
    /// UI grid — purely cosmetic, never used by the matching logic.</summary>
    public string DescriptionSnippet { get; init; } = string.Empty;

    /// <summary>Raw text of every column captured for this row at load time
    /// (see <see cref="Services.ExcelService"/>), keyed by 1-based Excel
    /// column number. Lets <see cref="SpecialComboRule"/> entries check
    /// whichever column the user configured for that specific rule, without
    /// the matching engine needing a direct dependency on the worksheet.
    /// Empty/missing keys read as an empty string.</summary>
    public IReadOnlyDictionary<int, string> RawColumns { get; init; } = new Dictionary<int, string>();

    // ---- Mutable reconciliation state -------------------------------------
    // These fields are written by the engine as passes run. A transaction is
    // "locked" the instant IsMatched flips true or Status leaves Unmatched;
    // see ReconciliationEngine remarks for the locking invariant.

    public bool IsMatched { get; set; }

    /// <summary>True if this row already had reconciliation comment text when
    /// the workbook was loaded AND Settings.IgnoreAlreadyReconciledRows is on.
    /// Pre-locked rows are excluded from every matching pass (via IsMatched)
    /// AND are never written to by IExcelService — whatever comment/format
    /// they already had is left completely untouched in the output file.</summary>
    public bool IsPreLocked { get; set; }

    public MatchStatus Status { get; set; } = MatchStatus.Unmatched;

    public string Comment { get; set; } = string.Empty;

    /// <summary>0-100. Written to the Confidence column next to the comment.</summary>
    public int ConfidenceScore { get; set; }

    /// <summary>Shared by every transaction in the same combination match, so
    /// the Excel service can paint them all with the same highlight color.
    /// -1 means "not part of a combination".</summary>
    public int GroupId { get; set; } = -1;

    public decimal AmountDollars => AmountCents / 100m;

    public bool IsCredit => AmountCents > 0;

    public override string ToString() =>
        $"{Side} row {RowNumber}: {Date:yyyy-MM-dd} {AmountDollars:C} [{Status}]";
}
