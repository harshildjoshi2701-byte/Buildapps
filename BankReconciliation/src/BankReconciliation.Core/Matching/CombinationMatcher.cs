using System.Diagnostics;
using BankReconciliation.Core.Models;

namespace BankReconciliation.Core.Matching;

/// <summary>
/// Two independent matching mechanisms live in this class:
///
///   <see cref="RunSpecialComboRules"/> — user-configured named/curated
///   rules (e.g. bank column 8 containing "Sysco" grouped against R365
///   column 16 containing "Online"), a pure keyword filter with no amount or
///   date check. Called directly and separately by
///   <see cref="ReconciliationEngine"/> as its own pipeline stage BEFORE
///   Pass 1 — see that method's remarks for why it runs first.
///
///   <see cref="ProcessAll"/> — the general, date-windowed subset-sum search
///   ("Pass 3" in the pipeline): for each bank transaction still unmatched
///   after Passes 1, 2, and the named-rule stage, searches for a SUBSET of
///   the still-unmatched, same-sign, in-date-window R365 transactions whose
///   amounts sum exactly to the bank transaction's amount. This is
///   subset-sum, which is NP-hard in general — the strategy below is layered
///   so the overwhelming majority of real-world cases resolve via a fast,
///   exact, polynomial-time path, and only the rare large/irregular case
///   falls through to a bounded search that is guaranteed to terminate
///   quickly by construction (see remarks on <see cref="FindCombination"/>).
///   Runs the sweep THREE times over — the first sweep can leave
///   transactions unresolved purely because other transactions were still
///   occupying shared candidate pools; once those are claimed (by an earlier
///   sweep, or by the named-rule stage before this class even ran), a later
///   sweep over the now-smaller remaining pool can succeed where the first
///   could not. A fourth sweep essentially never finds anything beyond what
///   three find, so three is the practical fixed point.
///
/// Search strategy per transaction, cheapest and most certain first:
///   1. Exact 2-combination via a hash table                — O(n)
///   2. Exact 3-combination via fix-one + hash-table lookup  — O(n) amortized
///   3. Bounded iterative subset-sum DP with same-sign monotonic pruning and
///      suffix-sum feasibility pruning, tracking MINIMUM CARDINALITY per
///      reachable sum so the result already satisfies "smallest number of
///      transactions" from the spec's tie-break priority — O(n x states),
///      hard-capped by <see cref="ReconciliationSettings.MaxDpStates"/> and
///      <see cref="ReconciliationSettings.PerTransactionTimeBudgetSeconds"/>.
///
/// Every match this class returns is re-verified to sum EXACTLY (within
/// AmountToleranceDollars) to the target before being accepted — that
/// invariant is never relaxed for speed, in any stage.
/// </summary>
public static class CombinationMatcher
{
    public enum Outcome { Found, None, Timeout }

    public sealed record SearchResult(IReadOnlyList<TransactionRecord>? Combination, bool Ambiguous, Outcome Outcome);

    public sealed class SearchStats
    {
        public long DpStatesProcessed;
        public long TwoSumHits;
        public long ThreeSumHits;
        public long DpHits;
        public long TruncatedPools;
        public long Aborted;
        public long ManualReviewCount;
        public long MatchedCount;

        public void MergeFrom(SearchStats other)
        {
            DpStatesProcessed += other.DpStatesProcessed;
            TwoSumHits += other.TwoSumHits;
            ThreeSumHits += other.ThreeSumHits;
            DpHits += other.DpHits;
            TruncatedPools += other.TruncatedPools;
            Aborted += other.Aborted;
            ManualReviewCount += other.ManualReviewCount;
            MatchedCount += other.MatchedCount;
        }
    }

    // ---- Stage 1 & 2: exact small-N combinations via hashing -----------------

    private static IReadOnlyList<TransactionRecord>? FindPair(long targetAbs, Dictionary<long, List<TransactionRecord>> byAmount)
    {
        foreach (var (amt, txns) in byAmount)
        {
            var complement = targetAbs - amt;
            if (complement < amt) continue;
            if (complement == amt)
            {
                if (txns.Count >= 2) return new[] { txns[0], txns[1] };
                continue;
            }
            if (byAmount.TryGetValue(complement, out var others) && others.Count > 0)
                return new[] { txns[0], others[0] };
        }
        return null;
    }

    private static IReadOnlyList<TransactionRecord>? FindTriple(long targetAbs, IReadOnlyList<TransactionRecord> pool, Dictionary<long, List<TransactionRecord>> byAmount)
    {
        foreach (var fixedTxn in pool)
        {
            var remaining = targetAbs - Math.Abs(fixedTxn.AmountCents);
            if (remaining <= 0) continue;

            foreach (var (amt, txns) in byAmount)
            {
                var complement = remaining - amt;
                if (complement < amt) continue;

                if (complement == amt)
                {
                    var candidates = txns.Where(t => !ReferenceEquals(t, fixedTxn)).Take(2).ToList();
                    if (candidates.Count == 2) return new[] { fixedTxn, candidates[0], candidates[1] };
                    continue;
                }

                if (!byAmount.TryGetValue(complement, out var others)) continue;
                var a = txns.FirstOrDefault(t => !ReferenceEquals(t, fixedTxn));
                if (a is null) continue;
                var b = others.FirstOrDefault(t => !ReferenceEquals(t, fixedTxn) && !ReferenceEquals(t, a));
                if (b is null) continue;
                return new[] { fixedTxn, a, b };
            }
        }
        return null;
    }

    // ---- Stage 3: bounded subset-sum DP ---------------------------------------

    private readonly record struct DpState(int Count, long ParentSum, int CandidateIndex, bool Tie);

    /// <summary>
    /// Finds a subset of <paramref name="candidates"/> summing exactly to
    /// <paramref name="targetCents"/> (same sign as the candidates; sign is
    /// implied since the pool is pre-filtered to one sign by the caller).
    /// </summary>
    public static SearchResult FindCombination(
        long targetCents,
        IReadOnlyList<TransactionRecord> candidates,
        ReconciliationSettings settings,
        SearchStats stats,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0 || targetCents == 0)
            return new SearchResult(null, false, Outcome.None);

        var targetAbs = Math.Abs(targetCents);
        var toleranceCents = MoneyMath.ToCents(settings.AmountToleranceDollars);
        var wantCredit = targetCents > 0;

        // Defensive same-sign filter: callers are expected to pre-filter to one
        // sign (ProcessAll's CandidatePoolFor does), but this method must
        // never rely on that discipline to keep its "every match sums exactly"
        // guarantee true — a wrong-signed candidate slipping in would corrupt
        // the magnitude-based search below (it buckets purely on Math.Abs).
        var fullPool = candidates
            .Where(c => c.AmountCents != 0 && (c.AmountCents > 0) == wantCredit)
            .Where(c => Math.Abs(c.AmountCents) <= targetAbs + toleranceCents)
            .OrderBy(c => c.Date).ThenBy(c => c.RowNumber)
            .ToList();
        if (fullPool.Count == 0) return new SearchResult(null, false, Outcome.None);
        if (fullPool.Sum(c => Math.Abs(c.AmountCents)) < targetAbs - toleranceCents)
            return new SearchResult(null, false, Outcome.None);

        bool truncated = false;
        List<TransactionRecord> pool;
        if (fullPool.Count > settings.MaxCombinationPoolSize)
        {
            // Keep only the candidates closest to the bank date — the most
            // business-plausible subset — rather than searching an arbitrary
            // partial view and risking a wrong answer.
            pool = fullPool.OrderByDescending(c => c.Date)
                           .Take(settings.MaxCombinationPoolSize)
                           .OrderBy(c => c.Date).ThenBy(c => c.RowNumber)
                           .ToList();
            truncated = true;
            Interlocked.Increment(ref stats.TruncatedPools);
        }
        else
        {
            pool = fullPool;
        }

        var byAmount = new Dictionary<long, List<TransactionRecord>>();
        foreach (var c in pool)
        {
            var amt = Math.Abs(c.AmountCents);
            if (!byAmount.TryGetValue(amt, out var list)) { list = new List<TransactionRecord>(); byAmount[amt] = list; }
            list.Add(c);
        }

        var pair = FindPair(targetAbs, byAmount);
        if (pair != null && VerifiesExactSum(pair, targetCents, toleranceCents))
        {
            Interlocked.Increment(ref stats.TwoSumHits);
            return new SearchResult(pair, false, Outcome.Found);
        }

        var triple = FindTriple(targetAbs, pool, byAmount);
        if (triple != null && VerifiesExactSum(triple, targetCents, toleranceCents))
        {
            Interlocked.Increment(ref stats.ThreeSumHits);
            return new SearchResult(triple, false, Outcome.Found);
        }

        if (truncated)
            return new SearchResult(null, false, Outcome.Timeout); // don't search a known-partial view; be honest instead of guessing

        var n = pool.Count;
        var suffixSum = new long[n + 1];
        for (int i = n - 1; i >= 0; i--)
            suffixSum[i] = suffixSum[i + 1] + Math.Abs(pool[i].AmountCents);

        var reachable = new Dictionary<long, DpState> { [0] = new DpState(0, -1, -1, false) };
        var sw = Stopwatch.StartNew();

        for (int idx = 0; idx < n; idx++)
        {
            if (cancellationToken.IsCancellationRequested)
                return new SearchResult(null, false, Outcome.Timeout);

            var camt = Math.Abs(pool[idx].AmountCents);
            // Snapshot BEFORE mutating — this is what makes the DP a correct
            // 0/1 knapsack (candidate idx can only be used once per path).
            var snapshot = reachable.ToArray();

            foreach (var kvp in snapshot)
            {
                var s = kvp.Key;
                var newSum = s + camt;
                if (newSum > targetAbs) continue; // same-sign monotonic pruning
                if (targetAbs - newSum > suffixSum[idx + 1]) continue; // cannot possibly complete to target

                var newCount = kvp.Value.Count + 1;
                if (reachable.TryGetValue(newSum, out var existing))
                {
                    if (newCount < existing.Count)
                        reachable[newSum] = new DpState(newCount, s, idx, false);
                    else if (newCount == existing.Count && existing.CandidateIndex != idx)
                        reachable[newSum] = existing with { Tie = true };
                }
                else
                {
                    reachable[newSum] = new DpState(newCount, s, idx, false);
                }

                stats.DpStatesProcessed++;
                if (reachable.Count > settings.MaxDpStates)
                {
                    Interlocked.Increment(ref stats.Aborted);
                    return new SearchResult(null, false, Outcome.Timeout);
                }
            }

            if (sw.Elapsed.TotalSeconds > settings.PerTransactionTimeBudgetSeconds)
            {
                Interlocked.Increment(ref stats.Aborted);
                return new SearchResult(null, false, Outcome.Timeout);
            }
            if (reachable.TryGetValue(targetAbs, out var hit) && hit.Count <= 2)
                break; // can't beat this (2-item exact already excluded by stage 1)
        }

        if (!reachable.ContainsKey(targetAbs))
            return new SearchResult(null, false, Outcome.None);

        Interlocked.Increment(ref stats.DpHits);
        var path = new List<TransactionRecord>();
        var cur = targetAbs;
        var ambiguous = false;
        while (cur != 0)
        {
            var state = reachable[cur];
            if (state.Tie) ambiguous = true;
            path.Add(pool[state.CandidateIndex]);
            cur = state.ParentSum;
        }

        if (!VerifiesExactSum(path, targetCents, toleranceCents))
        {
            // Should be unreachable given the same-sign filtering above, but a
            // match that doesn't actually sum correctly must never be
            // returned as Found — fail safe to None (-> Manual Review) rather
            // than risk posting a wrong reconciliation.
            return new SearchResult(null, false, Outcome.None);
        }
        return new SearchResult(path, ambiguous, Outcome.Found);
    }

    /// <summary>Final correctness gate applied before ANY combination is
    /// returned as a match — the one invariant this whole class exists to
    /// guarantee.</summary>
    private static bool VerifiesExactSum(IReadOnlyList<TransactionRecord> combination, long targetCents, long toleranceCents) =>
        Math.Abs(combination.Sum(c => c.AmountCents) - targetCents) <= toleranceCents;

    // ---- Stage A: named pairing rules ------------------------------------------

    /// <summary>
    /// Runs every user-configured <see cref="SpecialComboRule"/> (e.g. bank
    /// rows whose column 8 contains "Sysco" grouped against R365 rows whose
    /// column 16 contains "Online"). This is a pure keyword filter, not a
    /// search: every still-unmatched bank row containing that rule's bank
    /// keyword (in that rule's own bank column) and every still-unmatched
    /// R365 row containing that rule's R365 keyword (in that rule's own R365
    /// column) are grouped together as ONE match, unconditionally — no
    /// amount, sign, or date check of any kind, and not restricted to a date
    /// window. That is a deliberate, explicit design choice (not a search
    /// that happens to ignore date): these are curated, asserted business
    /// patterns where the presence of the keyword on both sides is itself
    /// considered sufficient evidence, so a rule must never produce No Match
    /// or Manual Review for a row it's eligible for. Any dollar gap between
    /// the two sides is still visible via the K/AB audit-formula columns.
    ///
    /// PUBLIC and called directly by <see cref="ReconciliationEngine"/> as
    /// its own first pipeline stage, BEFORE Pass 1 — curated rules represent
    /// asserted human knowledge, so they get first pick of the transaction
    /// pool ahead of anything Pass 1/2/3 would otherwise (possibly
    /// coincidentally) claim. Runs single-threaded — these pools are
    /// normally small relative to the whole file.
    /// </summary>
    public static void RunSpecialComboRules(
        IReadOnlyList<TransactionRecord> bankTransactions,
        IReadOnlyList<TransactionRecord> r365Transactions,
        ReconciliationSettings settings,
        GroupIdGenerator groupIds,
        SearchStats overallStats,
        CancellationToken cancellationToken)
    {
        if (settings.SpecialComboRules.Count == 0) return;

        foreach (var rule in settings.SpecialComboRules)
        {
            if (cancellationToken.IsCancellationRequested) return;
            if (string.IsNullOrWhiteSpace(rule.BankKeyword) || string.IsNullOrWhiteSpace(rule.R365Keyword))
                continue;

            var bankMatches = bankTransactions
                .Where(b => !b.IsMatched && b.AmountCents != 0 &&
                            RawColumnValue(b, rule.BankColumn).Contains(rule.BankKeyword, StringComparison.OrdinalIgnoreCase))
                .OrderBy(b => b.Date).ThenBy(b => b.RowNumber)
                .ToList();
            var r365Matches = r365Transactions
                .Where(r => !r.IsMatched &&
                            RawColumnValue(r, rule.R365Column).Contains(rule.R365Keyword, StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.Date).ThenBy(r => r.RowNumber)
                .ToList();

            // Nothing to group against on one side or the other — leave
            // these rows for later passes / eventual No Match rather than
            // manufacturing a match out of nothing.
            if (bankMatches.Count == 0 || r365Matches.Count == 0) continue;

            var ruleTag = $"{rule.BankKeyword}/{rule.R365Keyword}";
            var groupId = groupIds.Next();
            var comment = $"Matched (Named Rule — {ruleTag}, {bankMatches.Count + r365Matches.Count} Transactions Grouped)";

            foreach (var bank in bankMatches)
            {
                bank.IsMatched = true;
                bank.Status = MatchStatus.MatchedCombination;
                bank.Comment = comment;
                bank.ConfidenceScore = settings.SpecialComboConfidenceScore;
                bank.GroupId = groupId;
                Interlocked.Increment(ref overallStats.MatchedCount);
            }
            foreach (var r in r365Matches)
            {
                r.IsMatched = true;
                r.Status = MatchStatus.MatchedCombination;
                r.Comment = comment;
                r.ConfidenceScore = settings.SpecialComboConfidenceScore;
                r.GroupId = groupId;
            }
        }
    }

    /// <summary>Looks up a <see cref="SpecialComboRule"/>'s configured column
    /// in a transaction's captured raw column text, defaulting to empty for
    /// a column that wasn't captured (out of the configured capture width)
    /// or genuinely blank.</summary>
    private static string RawColumnValue(TransactionRecord t, int column) =>
        t.RawColumns.TryGetValue(column, out var value) ? value : string.Empty;

    // ---- Orchestration: general combination sweep over the whole file --------

    /// <summary>
    /// Runs the general date-windowed subset-sum sweep three times over (see
    /// class remarks for why more than once — each extra pass can only pick
    /// up matches the previous one left behind, since a fully-claimed pool
    /// can't produce more). Named rules (<see cref="RunSpecialComboRules"/>)
    /// are NOT run from here — <see cref="ReconciliationEngine"/> calls them
    /// separately, earlier in the pipeline. <paramref name="overallStats"/>
    /// is mutated in place so the caller can pass the same stats object used
    /// for the named-rule stage and get one combined total.
    /// </summary>
    public static void ProcessAll(
        IReadOnlyList<TransactionRecord> bankTransactions,
        IReadOnlyList<TransactionRecord> r365Transactions,
        ReconciliationSettings settings,
        GroupIdGenerator groupIds,
        IProgress<ReconciliationProgress>? progress,
        CancellationToken cancellationToken,
        SearchStats overallStats)
    {
        const int totalSweeps = 3;
        for (int sweepNumber = 1; sweepNumber <= totalSweeps; sweepNumber++)
            RunGeneralSweep(bankTransactions, r365Transactions, settings, groupIds, progress, cancellationToken, overallStats, sweepNumber, totalSweeps);
    }

    /// <summary>
    /// One full pass over every still-unmatched bank transaction, using the
    /// date-window-clustered parallel strategy described in the class
    /// remarks. <paramref name="overallStats"/> is mutated in place (merged
    /// into) rather than returned, so <see cref="ProcessAll"/> can call this
    /// repeatedly (currently 3 times) and accumulate combined statistics —
    /// each extra sweep only ever finds matches among rows the previous
    /// sweep left unmatched, since claimed rows are removed from the pool.
    ///
    /// Parallelism strategy: bank transactions are grouped into
    /// date-window clusters via interval merging (two transactions can only
    /// possibly compete for the same R365 candidate if their
    /// [Date - MaxDateDifferenceDays, Date] windows overlap). Clusters are
    /// therefore provably independent — they can never touch the same
    /// candidate pool — so different clusters are processed on different
    /// threads via Parallel.ForEach with zero locking required between them.
    /// WITHIN a cluster, transactions are processed strictly sequentially, in
    /// most-constrained-first order (smallest candidate pool first), so the
    /// locking/FIFO tie-break semantics are byte-for-byte what they'd be in a
    /// fully single-threaded run.
    /// </summary>
    private static void RunGeneralSweep(
        IReadOnlyList<TransactionRecord> bankTransactions,
        IReadOnlyList<TransactionRecord> r365Transactions,
        ReconciliationSettings settings,
        GroupIdGenerator groupIds,
        IProgress<ReconciliationProgress>? progress,
        CancellationToken cancellationToken,
        SearchStats overallStats,
        int sweepNumber,
        int totalSweeps)
    {
        var r365Pool = r365Transactions
            .Where(r => !r.IsMatched && (!settings.RequireGroupKeywordForCombinations || r.IsGroupedPosting))
            .ToList();
        var unmatchedBank = bankTransactions.Where(b => !b.IsMatched && b.AmountCents != 0).ToList();
        if (unmatchedBank.Count == 0) return;

        var credits = r365Pool.Where(r => r.AmountCents > 0).OrderBy(r => r.Date).ToList();
        var debits = r365Pool.Where(r => r.AmountCents < 0).OrderBy(r => r.Date).ToList();

        List<TransactionRecord> CandidatePoolFor(TransactionRecord bank)
        {
            var basePool = bank.AmountCents > 0 ? credits : debits;
            var lo = bank.Date.AddDays(-settings.MaxDateDifferenceDays);
            var result = new List<TransactionRecord>();
            foreach (var r in basePool)
            {
                if (r.IsMatched) continue;
                if (r.Date < lo || r.Date > bank.Date) continue;
                result.Add(r);
            }
            return result;
        }

        // ---- Build date-disjoint clusters (interval merge) ----
        var withWindows = unmatchedBank
            .Select(b => (Txn: b, WindowStart: b.Date.AddDays(-settings.MaxDateDifferenceDays)))
            .OrderBy(x => x.WindowStart)
            .ToList();

        var clusters = new List<List<TransactionRecord>>();
        List<TransactionRecord>? currentCluster = null;
        DateTime currentClusterEnd = DateTime.MinValue;
        foreach (var (txn, windowStart) in withWindows)
        {
            if (currentCluster is null || windowStart > currentClusterEnd)
            {
                currentCluster = new List<TransactionRecord>();
                clusters.Add(currentCluster);
                currentClusterEnd = txn.Date;
            }
            currentCluster.Add(txn);
            if (txn.Date > currentClusterEnd) currentClusterEnd = txn.Date;
        }

        var overallSw = Stopwatch.StartNew();
        var processedCount = 0;
        var lockObj = new object();
        var globalDeadlineHit = false;

        void ReportProgress()
        {
            if (progress is null) return;
            var elapsed = overallSw.Elapsed;
            var rate = processedCount / Math.Max(elapsed.TotalSeconds, 0.001);
            var remaining = rate > 0 ? TimeSpan.FromSeconds((unmatchedBank.Count - processedCount) / rate) : (TimeSpan?)null;
            progress.Report(new ReconciliationProgress
            {
                CurrentPass = 4,
                TotalPasses = 5,
                PassName = $"Combination matching (pass {sweepNumber} of {totalSweeps})",
                ProcessedCount = processedCount,
                TotalCount = unmatchedBank.Count,
                Elapsed = elapsed,
                EstimatedRemaining = remaining,
                StatusMessage = $"Searching combinations ({processedCount:N0}/{unmatchedBank.Count:N0})",
            });
        }

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, settings.MaxThreads),
            CancellationToken = cancellationToken,
        };

        try
        {
            Parallel.ForEach(clusters, parallelOptions, () => new SearchStats(), (cluster, loopState, localStats) =>
            {
                // Most-constrained-first WITHIN this cluster only — clusters
                // never share candidates, so this ordering doesn't need to
                // consider transactions outside the cluster.
                var ordered = cluster.OrderBy(b => CandidatePoolFor(b).Count).ToList();

                foreach (var bank in ordered)
                {
                    if (cancellationToken.IsCancellationRequested) { loopState.Stop(); break; }

                    lock (lockObj)
                    {
                        if (overallSw.Elapsed.TotalSeconds > settings.GlobalCombinationTimeBudgetSeconds)
                            globalDeadlineHit = true;
                    }
                    if (globalDeadlineHit)
                    {
                        if (!bank.IsMatched)
                        {
                            bank.Status = MatchStatus.ManualReview;
                            bank.Comment = "Manual Review (Global combination search time budget reached)";
                            localStats.ManualReviewCount++;
                        }
                        continue;
                    }

                    if (bank.IsMatched) continue; // defensive; should be unreachable given cluster independence
                    var pool = CandidatePoolFor(bank);
                    if (pool.Count == 0) continue; // falls through to NoMatch/PossibleDuplicate later

                    var result = FindCombination(bank.AmountCents, pool, settings, localStats, cancellationToken);

                    if (result.Outcome == Outcome.Timeout)
                    {
                        bank.Status = MatchStatus.ManualReview;
                        bank.Comment = "Manual Review (Combination search exceeded its time/complexity budget)";
                        localStats.ManualReviewCount++;
                    }
                    else if (result.Outcome == Outcome.Found && result.Combination is { Count: > 0 } combo)
                    {
                        var maxDiff = combo.Max(c => (bank.Date - c.Date).Days);
                        var confidence = ConfidenceScorer.Combination(combo.Count, maxDiff, settings.MaxDateDifferenceDays, result.Ambiguous);

                        if (result.Ambiguous || confidence < settings.MinCombinationConfidence)
                        {
                            bank.Status = MatchStatus.ManualReview;
                            bank.Comment = $"Manual Review (Tentative combination of {combo.Count} transactions found — confirm before posting)";
                            bank.ConfidenceScore = confidence;
                            localStats.ManualReviewCount++;
                        }
                        else
                        {
                            // Commit is safe without a lock: this cluster is the
                            // only thread that can ever touch these specific
                            // R365 rows (they were drawn from this cluster's
                            // date window, which by construction doesn't
                            // overlap any other cluster's window).
                            var groupId = groupIds.Next();
                            var comment = $"Matched (Combination of {combo.Count} Transactions)";
                            bank.IsMatched = true;
                            bank.Status = MatchStatus.MatchedCombination;
                            bank.Comment = comment;
                            bank.ConfidenceScore = confidence;
                            bank.GroupId = groupId;
                            foreach (var member in combo)
                            {
                                member.IsMatched = true;
                                member.Status = MatchStatus.MatchedCombination;
                                member.Comment = comment;
                                member.ConfidenceScore = confidence;
                                member.GroupId = groupId;
                            }
                            localStats.MatchedCount++;
                        }
                    }
                    // Outcome.None: leave Unmatched; DuplicateDetector / NoMatch
                    // finalization handles it after all passes complete.

                    lock (lockObj)
                    {
                        processedCount++;
                        if (processedCount % 100 == 0) ReportProgress();
                    }
                }
                return localStats;
            },
            localStats => { lock (lockObj) { overallStats.MergeFrom(localStats); } });
        }
        catch (OperationCanceledException)
        {
            // Cancellation is a normal, expected way for this method to end —
            // partial results already committed remain valid and locked.
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
        {
            // Parallel.ForEach wraps loop-body exceptions in AggregateException;
            // when every inner exception is a cancellation it's the same
            // "normal shutdown" case as above, just wrapped.
        }

        ReportProgress();
    }
}
