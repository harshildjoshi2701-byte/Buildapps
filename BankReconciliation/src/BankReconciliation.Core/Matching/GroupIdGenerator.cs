namespace BankReconciliation.Core.Matching;

/// <summary>
/// Thread-safe, per-run counter for match group IDs. Every match — one-to-one
/// or combination — gets a unique group ID from the same sequence, so
/// <see cref="ReconciliationEngine"/> can build the final list of
/// <see cref="Models.MatchGroup"/> objects with one simple GroupBy instead of
/// separate fragile lookup logic per match type, and the Excel service can
/// assign consistent highlight colors the same way for both.
///
/// Deliberately an instance (not a static field): a fresh instance is created
/// per <see cref="ReconciliationEngine"/> run, so concurrent runs (e.g. two
/// unit tests executing in parallel) never share counters and results stay
/// fully deterministic and reproducible.
/// </summary>
public sealed class GroupIdGenerator
{
    private int _next;

    public GroupIdGenerator(int startAt = 1) => _next = startAt - 1;

    public int Next() => Interlocked.Increment(ref _next);
}
