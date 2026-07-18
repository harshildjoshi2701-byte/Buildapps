using System.Diagnostics;
using BankReconciliation.Core.Models;

namespace BankReconciliation.Core.Matching;

/// <summary>
/// Orchestrates the full reconciliation pipeline:
///
///   Stage 1 — named/curated custom matching rules, unconditional keyword
///             grouping, no amount or date check, column-agnostic (not tied
///             to the Grouping column)                    (CombinationMatcher.RunSpecialComboRules)
///   Stage 2 — partition every remaining row by Grouping value, one
///             partition per distinct value plus one for blank            (GroupingPartitioner)
///   Stage 3 — WITHIN each partition, independently: exact amount + exact
///             date, one-to-one; then date-tolerant, one-to-one; then
///             bounded many-to-one combination search; then flag whatever
///             is still unmatched on both sides as Manual Review
///             (OneToOneMatcher, CombinationMatcher, GroupingPartitioner)
///   Stage 4 — duplicate detection + finalize every still-Unmatched row to
///             PossibleDuplicate or NoMatch                (DuplicateDetector)
///
/// Custom matching rules run FIRST, ahead of partitioning — they represent
/// curated, asserted business knowledge on an arbitrary column (not
/// necessarily Grouping), so they get first claim on the transaction pool.
///
/// GROUPING IS A PARTITION, NOT A MATCH TYPE. A non-blank Grouping value
/// means "these rows are only ever comparable to each other" — Stage 2 makes
/// that a hard boundary: two rows in different partitions are NEVER compared,
/// by construction (each partition gets its own private candidate pool for
/// every matcher in Stage 3). What used to be a single "sum the whole bucket
/// and compare totals" step is gone; instead each partition runs through
/// exactly the same real matching used for ungrouped rows. This matters
/// because a Grouping value is not always a true linking ID — it can be a
/// vendor/category tag (e.g. "Sysco") shared by a large, lopsided number of
/// rows on each side that individually match fine but whose TOTALS were
/// never going to tie out. See <see cref="GroupingPartitioner"/> remarks.
///
/// LOCKING INVARIANT: once a <see cref="TransactionRecord"/> has
/// <c>IsMatched == true</c> (or a terminal Status other than Unmatched), no
/// later stage may re-select it as a candidate. Every matcher enforces this by
/// filtering its candidate pools on <c>!IsMatched</c> at the moment it reads
/// them; nothing in this codebase ever "un-matches" a transaction once set.
/// This is the direct implementation of the spec's "Once a transaction has
/// been matched it must never be used again" rule, and it's what makes
/// per-partition processing safe: a partition only ever sees rows nothing
/// else has touched yet, and nothing outside it can touch them once it's
/// done — partitions are disjoint by construction (every row belongs to
/// exactly one Grouping value, or the blank one).
///
/// This class has no knowledge of Excel or the UI — see
/// <see cref="Services.IExcelService"/> for reading transactions in and
/// writing results back out.
/// </summary>
public sealed class ReconciliationEngine : IReconciliationEngine
{
    public Task<ReconciliationResult> RunAsync(
        IReadOnlyList<TransactionRecord> bankTransactions,
        IReadOnlyList<TransactionRecord> r365Transactions,
        ReconciliationSettings settings,
        IProgress<ReconciliationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Run(bankTransactions, r365Transactions, settings, progress, cancellationToken), cancellationToken);
    }

    private static ReconciliationResult Run(
        IReadOnlyList<TransactionRecord> allBank,
        IReadOnlyList<TransactionRecord> allR365,
        ReconciliationSettings settings,
        IProgress<ReconciliationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var log = new ReconciliationLog();
        var overallSw = Stopwatch.StartNew();
        log.Info($"Reconciliation started. Bank rows: {allBank.Count:N0}, R365 rows: {allR365.Count:N0}.");

        // "Ignore Already Reconciled Rows": rows that arrived with a non-empty
        // comment already in the comment column are treated as pre-locked —
        // excluded from every stage, left completely untouched, not counted as
        // unmatched. IExcelService is responsible for setting IsMatched=true
        // and Status=already-final on these BEFORE calling into the engine
        // when this setting is enabled; the engine just has to make sure it
        // never touches them, which the standard `!IsMatched` filtering in
        // every matcher already guarantees.
        var preLockedBank = allBank.Count(b => b.IsMatched);
        var preLockedR365 = allR365.Count(r => r.IsMatched);
        if (preLockedBank > 0 || preLockedR365 > 0)
            log.Info($"Skipping {preLockedBank:N0} bank / {preLockedR365:N0} R365 rows already reconciled (Ignore Already Reconciled Rows is on).");

        cancellationToken.ThrowIfCancellationRequested();

        // Single shared, per-run group-ID sequence used by every stage, so
        // BuildMatchGroups below can group purely by GroupId regardless of
        // which stage found the match.
        var groupIds = new GroupIdGenerator();
        var comboStats = new CombinationMatcher.SearchStats();

        // ---- Stage 1: custom matching rules (named, curated, unconditional, column-agnostic) ----
        var stage1Sw = Stopwatch.StartNew();
        ReportSimple(progress, 1, 4, "Custom matching rules", 0, allBank.Count, overallSw.Elapsed);
        var enteringStage1 = allBank.Count(b => !b.IsMatched) + allR365.Count(r => !r.IsMatched);
        CombinationMatcher.RunSpecialComboRules(allBank, allR365, settings, groupIds, comboStats, cancellationToken);
        var remainingAfterStage1 = allBank.Count(b => !b.IsMatched) + allR365.Count(r => !r.IsMatched);
        LogStage(log, "Custom matching rules", enteringStage1, enteringStage1 - remainingAfterStage1, skipped: 0, remainingAfterStage1, stage1Sw.Elapsed);
        ReportSimple(progress, 1, 4, "Custom matching rules", allBank.Count, allBank.Count, overallSw.Elapsed);
        cancellationToken.ThrowIfCancellationRequested();

        // ---- Stage 2: partition every remaining row by Grouping value ----
        var partitions = GroupingPartitioner.BuildPartitions(allBank, allR365);
        var groupedPartitions = partitions.Count(p => p.IsGrouped);
        log.Info($"Stage 2 (partition by Grouping): {groupedPartitions:N0} named group(s) + 1 ungrouped partition.");

        // ---- Stage 3: within each partition, independently: exact -> date-tolerant -> combination ----
        var stage3Sw = Stopwatch.StartNew();
        var enteringStage3 = remainingAfterStage1;
        int oneToOneCount = 0, groupingScopedMatches = 0;
        var combinationDeadline = Stopwatch.StartNew();
        var combinationBudgetWarningLogged = false;

        for (int i = 0; i < partitions.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested) break;
            var partition = partitions[i];
            if (partition.RowCount == 0) continue;

            var partitionSw = Stopwatch.StartNew();
            var partitionEntering = partition.RowCount;
            var partitionGenuineBefore = CountGenuinelyMatched(partition);

            var exact = OneToOneMatcher.MatchExact(partition.Bank, partition.R365, groupIds);
            var dateTolerant = OneToOneMatcher.MatchDateTolerant(partition.Bank, partition.R365, settings.MaxDateDifferenceDays, groupIds);

            // Cross-partition safety valve: combination search is bounded
            // PER call (pool size, DP states, per-transaction and per-call
            // time budgets — see CombinationMatcher), but with a grouping
            // value per real-world vendor/category there can easily be a
            // hundred-plus partitions in one run. Once the SAME
            // GlobalCombinationTimeBudgetSeconds has been spent in aggregate
            // across every partition's combination search this run, stop
            // starting new ones — remaining partitions' still-unmatched rows
            // fall through to the normal residual-flagging / No Match path
            // below rather than being forced. Rule: the app must stay
            // responsive with several thousand rows regardless of how many
            // distinct Grouping values they're split across.
            if (combinationDeadline.Elapsed.TotalSeconds < settings.GlobalCombinationTimeBudgetSeconds)
            {
                CombinationMatcher.ProcessAll(partition.Bank, partition.R365, settings, groupIds, progress: null, cancellationToken, comboStats);
            }
            else if (!combinationBudgetWarningLogged)
            {
                combinationBudgetWarningLogged = true;
                log.Warn($"Global combination search time budget ({settings.GlobalCombinationTimeBudgetSeconds:F0}s) reached after {i:N0}/{partitions.Count:N0} partitions; remaining partitions skip combination search and fall through to residual review / No Match.");
            }

            GroupingPartitioner.FlagUnresolvedResidual(partition);

            oneToOneCount += exact + dateTolerant;
            var partitionGenuineAfter = CountGenuinelyMatched(partition);
            if (partition.IsGrouped) groupingScopedMatches += partitionGenuineAfter - partitionGenuineBefore;

            if (partitionEntering >= 10 || partitionGenuineAfter > partitionGenuineBefore)
            {
                log.Info($"  Partition \"{(partition.IsGrouped ? partition.Key : "(ungrouped)")}\": {partitionEntering:N0} in "
                    + $"({partition.Bank.Count:N0} bank / {partition.R365.Count:N0} R365), "
                    + $"{partitionGenuineAfter - partitionGenuineBefore:N0} matched, "
                    + $"{partition.Bank.Count(b => !b.IsMatched) + partition.R365.Count(r => !r.IsMatched):N0} remaining, "
                    + $"{partitionSw.Elapsed.TotalSeconds:F2}s.");
            }

            if ((i + 1) % 25 == 0 || i == partitions.Count - 1)
            {
                progress?.Report(new ReconciliationProgress
                {
                    CurrentPass = 3,
                    TotalPasses = 4,
                    PassName = "Matching within Grouping partitions",
                    ProcessedCount = i + 1,
                    TotalCount = partitions.Count,
                    Elapsed = overallSw.Elapsed,
                    StatusMessage = $"Partition {i + 1:N0}/{partitions.Count:N0}",
                });
            }
        }

        var remainingAfterStage3 = allBank.Count(b => !b.IsMatched) + allR365.Count(r => !r.IsMatched);
        LogStage(log, "Matching within Grouping partitions", enteringStage3, enteringStage3 - remainingAfterStage3, skipped: 0, remainingAfterStage3, stage3Sw.Elapsed);
        log.Info(
            $"  Breakdown: {oneToOneCount:N0} one-to-one (exact + date-tolerant), " +
            $"{comboStats.MatchedCount:N0} combination matches total (named rules + within-partition search), " +
            $"{comboStats.ManualReviewCount:N0} flagged for manual review by combination search " +
            $"(two-item={comboStats.TwoSumHits:N0}, three-item={comboStats.ThreeSumHits:N0}, " +
            $"larger-via-DP={comboStats.DpHits:N0}, search-aborted={comboStats.Aborted:N0}, " +
            $"pool-truncated={comboStats.TruncatedPools:N0}).");
        if (comboStats.Aborted > 0)
            log.Warn($"{comboStats.Aborted:N0} combination searches exceeded their time/state budget and were routed to Manual Review instead of being forced.");
        if (comboStats.TruncatedPools > 0)
            log.Warn($"{comboStats.TruncatedPools:N0} transactions had more candidate R365 rows than MaxCombinationPoolSize ({settings.MaxCombinationPoolSize}); only the closest-dated candidates were searched.");
        cancellationToken.ThrowIfCancellationRequested();

        // ---- Stage 4: duplicate detection + finalize remaining rows ----
        var stage4Sw = Stopwatch.StartNew();
        ReportSimple(progress, 4, 4, "Duplicate detection & finalizing", 0, allBank.Count + allR365.Count, overallSw.Elapsed);
        var enteringStage4 = allBank.Count(b => b.Status == MatchStatus.Unmatched) + allR365.Count(r => r.Status == MatchStatus.Unmatched);
        DuplicateDetector.FinalizeUnmatched(allBank);
        DuplicateDetector.FinalizeUnmatched(allR365);
        var dupBank = allBank.Count(b => b.Status == MatchStatus.PossibleDuplicate);
        var dupR365 = allR365.Count(r => r.Status == MatchStatus.PossibleDuplicate);
        LogStage(log, "Duplicate detection & finalizing", enteringStage4, matched: 0, skipped: 0, remaining: enteringStage4, stage4Sw.Elapsed);
        log.Info($"Possible duplicates flagged: {dupBank:N0} bank, {dupR365:N0} R365.");
        ReportSimple(progress, 4, 4, "Duplicate detection & finalizing", allBank.Count + allR365.Count, allBank.Count + allR365.Count, overallSw.Elapsed);

        // ---- Build match groups from final state ----
        var matchGroups = BuildMatchGroups(allBank, allR365);

        // ---- Summary ----
        overallSw.Stop();
        var summary = new ReconciliationSummary
        {
            TotalBankTransactions = allBank.Count,
            TotalR365Transactions = allR365.Count,
            MatchedBankTransactions = allBank.Count(b => IsGenuinelyMatched(b.Status)),
            MatchedR365Transactions = allR365.Count(r => IsGenuinelyMatched(r.Status)),
            MatchedAmount = allBank.Where(b => IsGenuinelyMatched(b.Status)).Sum(b => Math.Abs(b.AmountDollars)),
            UnmatchedBankTransactions = allBank.Count(b => b.Status == MatchStatus.NoMatch),
            UnmatchedR365Transactions = allR365.Count(r => r.Status == MatchStatus.NoMatch),
            OneToOneMatches = oneToOneCount,
            CombinationMatches = (int)comboStats.MatchedCount,
            GroupingMatches = groupingScopedMatches,
            ManualReviewCount = allBank.Count(b => b.Status == MatchStatus.ManualReview) + allR365.Count(r => r.Status == MatchStatus.ManualReview),
            PossibleDuplicateBankCount = dupBank,
            PossibleDuplicateR365Count = dupR365,
            ProcessingTime = overallSw.Elapsed,
            ErrorCount = log.ErrorCount,
            WarningCount = log.WarningCount,
        };

        log.RowsProcessed = allBank.Count + allR365.Count;
        log.RowsMatched = summary.MatchedBankTransactions + summary.MatchedR365Transactions;
        log.ProcessingTime = overallSw.Elapsed;
        log.Info(
            $"Reconciliation complete in {overallSw.Elapsed.TotalSeconds:F2}s. " +
            $"Bank matched {summary.MatchedBankTransactions:N0}/{summary.TotalBankTransactions:N0} " +
            $"({summary.BankMatchPercentage:F1}%). R365 matched {summary.MatchedR365Transactions:N0}/{summary.TotalR365Transactions:N0} " +
            $"({summary.R365MatchPercentage:F1}%).");

        return new ReconciliationResult
        {
            Summary = summary,
            MatchGroups = matchGroups,
            BankTransactions = allBank,
            R365Transactions = allR365,
            Log = log,
        };
    }

    private static int CountGenuinelyMatched(GroupingPartitioner.Partition partition) =>
        partition.Bank.Count(b => IsGenuinelyMatched(b.Status)) + partition.R365.Count(r => IsGenuinelyMatched(r.Status));

    /// <summary>True for the three terminal statuses that represent an actual
    /// match (exact, date-tolerant, or combination). Deliberately NOT the
    /// same thing as <see cref="TransactionRecord.IsMatched"/>: that flag
    /// also covers rows a stage has merely finished deciding about — e.g.
    /// <see cref="GroupingPartitioner.FlagUnresolvedResidual"/> never sets it
    /// at all (a residual flag is explicitly not a match), and pre-locked
    /// rows set it for an unrelated reason. Anything reporting "how many
    /// matched" — this summary, <see cref="BuildMatchGroups"/> — must use
    /// Status, not IsMatched, or a NoMatch/ManualReview row could get counted
    /// as both matched and unmatched at once. Mirrors the same tri-state
    /// check the UI already uses (MainViewModel.DisplaySortRank).</summary>
    private static bool IsGenuinelyMatched(MatchStatus status) =>
        status is MatchStatus.MatchedExact or MatchStatus.MatchedDateTolerant or MatchStatus.MatchedCombination;

    /// <summary>Every matched transaction — one-to-one or combination — carries
    /// a GroupId from the same shared sequence (see <see cref="GroupIdGenerator"/>),
    /// assigned atomically with locking at match time. Building the final
    /// group list is therefore a simple, unambiguous GroupBy: no separate
    /// lookup heuristic is needed to figure out which R365 row(s) belong to
    /// which bank row.
    ///
    /// KNOWN GAP: this method is anchored on Bank transactions — it produces
    /// one <see cref="MatchGroup"/> per matched Bank row, with every R365 row
    /// sharing its GroupId attached as a member. A named rule with MULTIPLE
    /// Bank rows produces one MatchGroup per Bank row, each duplicating the
    /// same R365 members; an R365-only match (impossible for a genuine match
    /// by construction — see IsGenuinelyMatched — but relevant to note) has
    /// no Bank row to anchor on and produces NO MatchGroup. A UI that needs
    /// an accurate one-sided-group view should read directly from the
    /// R365/Bank transaction lists' Status, not this method, until/unless
    /// MatchGroup is generalized to N-vs-M.</summary>
    private static List<MatchGroup> BuildMatchGroups(IReadOnlyList<TransactionRecord> bank, IReadOnlyList<TransactionRecord> r365)
    {
        var r365ByGroup = r365.Where(r => r.GroupId >= 0).GroupBy(r => r.GroupId).ToDictionary(g => g.Key, g => (IReadOnlyList<TransactionRecord>)g.ToList());
        var groups = new List<MatchGroup>();

        foreach (var b in bank.Where(b => b.IsMatched && b.GroupId >= 0))
        {
            r365ByGroup.TryGetValue(b.GroupId, out var members);
            groups.Add(new MatchGroup
            {
                GroupId = b.GroupId,
                Status = b.Status,
                BankTransaction = b,
                R365Transactions = members ?? Array.Empty<TransactionRecord>(),
                ConfidenceScore = b.ConfidenceScore,
                Comment = b.Comment,
            });
        }
        return groups;
    }

    private static void ReportSimple(IProgress<ReconciliationProgress>? progress, int pass, int totalPasses, string name, int processed, int total, TimeSpan elapsed)
    {
        progress?.Report(new ReconciliationProgress
        {
            CurrentPass = pass,
            TotalPasses = totalPasses,
            PassName = name,
            ProcessedCount = processed,
            TotalCount = total,
            Elapsed = elapsed,
            StatusMessage = $"{name}…",
        });
    }

    /// <summary>Single place every stage logs its required six fields (stage
    /// name, entering, matched, skipped, remaining, time taken) so the format
    /// is consistent and none of the callers can drift out of sync with each
    /// other.</summary>
    private static void LogStage(ReconciliationLog log, string stageName, int entering, int matched, int skipped, int remaining, TimeSpan elapsed) =>
        log.Info($"Stage [{stageName}]: entering={entering:N0}, matched={matched:N0}, skipped={skipped:N0}, remaining={remaining:N0}, time={elapsed.TotalSeconds:F2}s.");
}
