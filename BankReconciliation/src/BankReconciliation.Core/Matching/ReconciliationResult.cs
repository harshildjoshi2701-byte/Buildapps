using BankReconciliation.Core.Models;

namespace BankReconciliation.Core.Matching;

/// <summary>Everything a caller needs after a run: the summary for the
/// dashboard, every resolved match group (for the Excel highlight step and
/// the UI grid), the full transaction lists (every row, matched or not, in
/// original order) and the structured log.</summary>
public sealed class ReconciliationResult
{
    public required ReconciliationSummary Summary { get; init; }
    public required IReadOnlyList<MatchGroup> MatchGroups { get; init; }
    public required IReadOnlyList<TransactionRecord> BankTransactions { get; init; }
    public required IReadOnlyList<TransactionRecord> R365Transactions { get; init; }
    public required ReconciliationLog Log { get; init; }
}
