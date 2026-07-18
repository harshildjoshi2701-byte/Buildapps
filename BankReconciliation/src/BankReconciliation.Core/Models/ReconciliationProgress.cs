namespace BankReconciliation.Core.Models;

/// <summary>
/// Snapshot reported via <see cref="IProgress{T}"/> while a reconciliation
/// run is in flight, so the UI can drive a progress bar / status line /
/// elapsed-and-remaining-time display without polling.
/// </summary>
public sealed class ReconciliationProgress
{
    public required int CurrentPass { get; init; }
    public required int TotalPasses { get; init; }
    public required string PassName { get; init; }
    public required int ProcessedCount { get; init; }
    public required int TotalCount { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public TimeSpan? EstimatedRemaining { get; init; }
    public string StatusMessage { get; init; } = string.Empty;

    /// <summary>0-100 overall progress across all passes, for a single progress bar.</summary>
    public double OverallPercent
    {
        get
        {
            if (TotalPasses <= 0) return 0;
            var perPass = 100.0 / TotalPasses;
            var withinPass = TotalCount == 0 ? perPass : perPass * ProcessedCount / TotalCount;
            return Math.Clamp((CurrentPass - 1) * perPass + withinPass, 0, 100);
        }
    }
}
