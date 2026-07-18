using System.Diagnostics;
using BankReconciliation.Core.Models;

namespace BankReconciliation.Core.Matching;

/// <summary>
/// Orchestrates the full reconciliation pipeline:
///
///   Pass 1 — named/curated custom matching rules, unconditional keyword
///            grouping, no amount or date check                  (CombinationMatcher.RunSpecialComboRules)
///   Pass 2 — one-to-one, exact amount, exact date                (OneToOneMatcher)
///   Pass 3 — one-to-one, exact amount, date within window         (OneToOneMatcher)
///   Pass 4 — general combination matching, date-windowed subset-sum
///            (CombinationMatcher.ProcessAll)
///   Pass 5 — duplicate detection + finalize every still-Unmatched row
///            to PossibleDuplicate or NoMatch                    (DuplicateDetector)
///
/// Custom matching rules run FIRST, ahead of even the exact-match pass —
/// they represent curated, asserted business knowledge (e.g. "bank rows
/// mentioning Sysco always belong with R365 rows mentioning Online"), so
/// they get first claim on the transaction pool. Without this, a handful of
/// rows a named rule would otherwise group together can instead get peeled
/// off individually by Pass 2/3's exact-amount matching whenever a
/// coincidental amount+date collision exists, which fragments what should
/// have been one clean, fully-explained group into a mix of small exact
/// matches plus a named-rule group with an unexplained residual gap.
///
/// LOCKING INVARIANT: once a <see cref="TransactionRecord"/> has
/// <c>IsMatched == true</c> (or a terminal Status other than Unmatched), no
/// later pass may re-select it as a candidate. Every matcher enforces this by
/// filtering its candidate pools on <c>!IsMatched</c> at the moment it reads
/// them; nothing in this codebase ever "un-matches" a transaction once set.
/// This is the direct implementation of the spec's "Once a transaction has
/// been matched it must never be used again" rule.
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
        // excluded from every pass, left completely untouched, not counted as
        // unmatched. IExcelService is responsible for setting IsMatched=true
        // and Status=already-final on these BEFORE calling into the engine
        // when this setting is enabled; the engine just has to make sure it
        // never touches them, which the standard `!IsMatched` filtering in
        // every matcher already guarantees. We only log the count here.
        var preLockedBank = allBank.Count(b => b.IsMatched);
        var preLockedR365 = allR365.Count(r => r.IsMatched);
        if (preLockedBank > 0 || preLockedR365 > 0)
            log.Info($"Skipping {preLockedBank:N0} bank / {preLockedR365:N0} R365 rows already reconciled (Ignore Already Reconciled Rows is on).");

        cancellationToken.ThrowIfCancellationRequested();

        // Single shared, per-run group-ID sequence used by every pass, so
        // BuildMatchGroups below can group purely by GroupId regardless of
        // whether a match came from Pass 1, 2, 3, or 4.
        var groupIds = new GroupIdGenerator();

        // Shared across the named-rule stage and the general combination
        // sweep so the final counts/log reflect combination-style matching
        // as a whole, regardless of which of the two stages found it.
        var comboStats = new SearchStats();

        // ---- Pass 1: custom matching rules (named, curated, unconditional) ----
        ReportSimple(progress, 1, 5, "Custom matching rules", 0, allBank.Count, overallSw.Elapsed);
        CombinationMatcher.RunSpecialComboRules(allBank, allR365, settings, groupIds, comboStats, cancellationToken);
        log.Info($"Pass 1 (custom matching rules): {comboStats.MatchedCount:N0} rows grouped by named rules so far.");
        ReportSimple(progress, 1, 5, "Custom matching rules", allBank.Count, allBank.Count, overallSw.Elapsed);
        cancellationToken.ThrowIfCancellationRequested();

        // ---- Pass 2: exact date + exact amount, one-to-one ----
        ReportSimple(progress, 2, 5, "Exact matching", 0, allBank.Count, overallSw.Elapsed);
        var exactMatchCount = OneToOneMatcher.MatchExact(allBank, allR365, groupIds);
        log.Info($"Pass 2 (exact date + amount): {exactMatchCount:N0} one-to-one matches.");
        ReportSimple(progress, 2, 5, "Exact matching", allBank.Count, allBank.Count, overallSw.Elapsed);
        cancellationToken.ThrowIfCancellationRequested();

        // ---- Pass 3: date-tolerant, one-to-one ----
        ReportSimple(progress, 3, 5, "Date-tolerant matching", 0, allBank.Count, overallSw.Elapsed);
        var dateTolerantMatchCount = OneToOneMatcher.MatchDateTolerant(allBank, allR365, settings.MaxDateDifferenceDays, groupIds);
        log.Info($"Pass 3 (date-tolerant, 0-{settings.MaxDateDifferenceDays} days): {dateTolerantMatchCount:N0} one-to-one matches.");
        ReportSimple(progress, 3, 5, "Date-tolerant matching", allBank.Count, allBank.Count, overallSw.Elapsed);
        cancellationToken.ThrowIfCancellationRequested();

        // ---- Pass 4: general combination matching (date-windowed subset-sum) ----
        var generalSweepSw = Stopwatch.StartNew();
        CombinationMatcher.ProcessAll(allBank, allR365, settings, groupIds, progress, cancellationToken, comboStats);
        log.Info(
            $"Pass 4 (combination/subset-sum): {comboStats.MatchedCount:N0} combination matches total " +
            $"(named rules + general sweep), {comboStats.ManualReviewCount:N0} flagged for manual review, " +
            $"general sweep took {generalSweepSw.Elapsed.TotalSeconds:F2}s " +
            $"(two-item={comboStats.TwoSumHits:N0}, three-item={comboStats.ThreeSumHits:N0}, " +
            $"larger-via-DP={comboStats.DpHits:N0}, search-aborted={comboStats.Aborted:N0}, " +
            $"pool-truncated={comboStats.TruncatedPools:N0}).");
        if (comboStats.Aborted > 0)
            log.Warn($"{comboStats.Aborted:N0} combination searches exceeded their time/state budget and were routed to Manual Review instead of being forced.");
        if (comboStats.TruncatedPools > 0)
            log.Warn($"{comboStats.TruncatedPools:N0} transactions had more candidate R365 rows than MaxCombinationPoolSize ({settings.MaxCombinationPoolSize}); only the closest-dated candidates were searched.");
        cancellationToken.ThrowIfCancellationRequested();

        // ---- Pass 5: duplicate detection + finalize remaining rows ----
        ReportSimple(progress, 5, 5, "Duplicate detection & finalizing", 0, allBank.Count + allR365.Count, overallSw.Elapsed);
        DuplicateDetector.FinalizeUnmatched(allBank);
        DuplicateDetector.FinalizeUnmatched(allR365);
        var dupBank = allBank.Count(b => b.Status == MatchStatus.PossibleDuplicate);
        var dupR365 = allR365.Count(r => r.Status == MatchStatus.PossibleDuplicate);
        log.Info($"Possible duplicates flagged: {dupBank:N0} bank, {dupR365:N0} R365.");
        ReportSimple(progress, 5, 5, "Duplicate detection & finalizing", allBank.Count + allR365.Count, allBank.Count + allR365.Count, overallSw.Elapsed);

        // ---- Build match groups from final state ----
        var matchGroups = BuildMatchGroups(allBank, allR365);

        // ---- Summary ----
        overallSw.Stop();
        var summary = new ReconciliationSummary
        {
            TotalBankTransactions = allBank.Count,
            TotalR365Transactions = allR365.Count,
            MatchedBankTransactions = allBank.Count(b => b.IsMatched),
            MatchedR365Transactions = allR365.Count(r => r.IsMatched),
            MatchedAmount = allBank.Where(b => b.IsMatched).Sum(b => Math.Abs(b.AmountDollars)),
            UnmatchedBankTransactions = allBank.Count(b => b.Status == MatchStatus.NoMatch),
            UnmatchedR365Transactions = allR365.Count(r => r.Status == MatchStatus.NoMatch),
            OneToOneMatches = exactMatchCount + dateTolerantMatchCount,
            CombinationMatches = (int)comboStats.MatchedCount,
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

    /// <summary>Every matched transaction — one-to-one or combination — carries
    /// a GroupId from the same shared sequence (see <see cref="GroupIdGenerator"/>),
    /// assigned atomically with locking at match time. Building the final
    /// group list is therefore a simple, unambiguous GroupBy: no separate
    /// lookup heuristic is needed to figure out which R365 row(s) belong to
    /// which bank row.</summary>
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
}
