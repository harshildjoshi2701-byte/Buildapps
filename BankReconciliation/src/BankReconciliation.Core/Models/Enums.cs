namespace BankReconciliation.Core.Models;

/// <summary>
/// Which side of the reconciliation a transaction belongs to.
/// </summary>
public enum TransactionSide
{
    Bank,
    R365
}

/// <summary>
/// Final reconciliation outcome for a single transaction row. Drives both the
/// cell comment text and the highlight color written back to the workbook.
/// </summary>
public enum MatchStatus
{
    /// <summary>Not yet processed (initial state).</summary>
    Unmatched,

    /// <summary>One-to-one, exact amount, exact date. Green.</summary>
    MatchedExact,

    /// <summary>One-to-one, exact amount, date within the allowed window. Green.</summary>
    MatchedDateTolerant,

    /// <summary>One bank transaction matched to a combination of 2+ R365
    /// transactions whose amounts sum exactly to it. Blue-family (per-group color).</summary>
    MatchedCombination,

    /// <summary>A tentative combination or ambiguous match was found but confidence
    /// was too low, or a valid combination could not be resolved within the
    /// engine's search budget. Needs a human to look at it. Yellow.</summary>
    ManualReview,

    /// <summary>Unmatched, and shares its date+amount with at least one other
    /// unmatched transaction on the same side. Yellow (a special case of
    /// Manual Review — see ReconciliationEngine remarks).</summary>
    PossibleDuplicate,

    /// <summary>No candidate could be found at all. Red.</summary>
    NoMatch
}

/// <summary>Severity level for ReconciliationLogEntry.</summary>
public enum LogLevel
{
    Info,
    Warning,
    Error
}
