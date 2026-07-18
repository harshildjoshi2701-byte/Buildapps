using BankReconciliation.Core.Models;

namespace BankReconciliation.Core.Matching;

/// <summary>
/// The reconciliation engine's public contract. Deliberately independent of
/// Excel, WPF, or any I/O — it takes plain in-memory transaction lists and
/// returns a plain in-memory result, so it can be driven by the desktop app,
/// a unit test, a future CLI, or a future web service without change. Excel
/// reading/writing lives entirely in <see cref="Services.IExcelService"/>.
/// </summary>
public interface IReconciliationEngine
{
    /// <summary>
    /// Runs all three matching passes plus duplicate detection over the given
    /// transactions IN PLACE (the same <see cref="TransactionRecord"/>
    /// instances passed in are mutated with their final Status/Comment/etc.)
    /// and returns the aggregated result.
    /// </summary>
    Task<ReconciliationResult> RunAsync(
        IReadOnlyList<TransactionRecord> bankTransactions,
        IReadOnlyList<TransactionRecord> r365Transactions,
        ReconciliationSettings settings,
        IProgress<ReconciliationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
