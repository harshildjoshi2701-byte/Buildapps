using BankReconciliation.Core.Models;

namespace BankReconciliation.Core.Matching;

/// <summary>
/// Implements exact matching (exact amount + exact date) and date-tolerant
/// matching (exact amount + date within the allowed window). Both are strict
/// one-to-one: exactly one bank transaction to exactly one R365 transaction,
/// no combinations — this applies to EVERY R365 row regardless of whether
/// it's flagged as a grouped posting (that flag only changes what combination
/// search is allowed to do with the *leftovers*; every row is still eligible
/// for a plain 1:1 match first). Called once per Grouping partition by
/// <see cref="ReconciliationEngine"/> (see its remarks) — every candidate
/// list passed in is already scoped to a single partition, so there is
/// nothing partition-specific in this class itself.
///
/// Both methods share the same shape: index the unmatched R365 pool by exact
/// signed amount (O(1) average lookup), then for each unmatched bank
/// transaction look up its amount bucket and pick the best candidate by the
/// tie-break priority from the spec: exact date, fewest transactions (always
/// 1 here), oldest R365 first (FIFO), smallest date difference, highest
/// confidence. See <see cref="SelectionKey"/>.
/// </summary>
public static class OneToOneMatcher
{
    /// <summary>
    /// Builds an index from signed-amount-in-cents to the list of currently
    /// unmatched R365 transactions with that exact amount, each list sorted
    /// FIFO (oldest date first, then lowest row number) so the first
    /// candidate found is already the FIFO-preferred one.
    /// </summary>
    private static Dictionary<long, List<TransactionRecord>> IndexByAmount(IEnumerable<TransactionRecord> r365)
    {
        var index = new Dictionary<long, List<TransactionRecord>>();
        foreach (var r in r365)
        {
            if (!index.TryGetValue(r.AmountCents, out var list))
            {
                list = new List<TransactionRecord>();
                index[r.AmountCents] = list;
            }
            list.Add(r);
        }
        foreach (var list in index.Values)
            list.Sort((a, b) => a.Date != b.Date ? a.Date.CompareTo(b.Date) : a.RowNumber.CompareTo(b.RowNumber));
        return index;
    }

    /// <summary>Tuple used to rank candidates: lower is better. Mirrors the
    /// spec's stated priority order exactly: exact date match, fewest
    /// transactions, oldest (FIFO), smallest date difference, highest
    /// confidence (negated so "higher confidence" sorts as "lower key").</summary>
    private readonly record struct SelectionKey(int NotExactDate, int TransactionCount, DateTime OldestDate, int DateDiff, int NegConfidence)
        : IComparable<SelectionKey>
    {
        public int CompareTo(SelectionKey other)
        {
            int c = NotExactDate.CompareTo(other.NotExactDate);
            if (c != 0) return c;
            c = TransactionCount.CompareTo(other.TransactionCount);
            if (c != 0) return c;
            c = OldestDate.CompareTo(other.OldestDate);
            if (c != 0) return c;
            c = DateDiff.CompareTo(other.DateDiff);
            if (c != 0) return c;
            return NegConfidence.CompareTo(other.NegConfidence);
        }
    }

    /// <summary>
    /// Exact amount, exact date, one-to-one. Locks both sides immediately on
    /// every match. Returns the number of matches made.
    /// </summary>
    public static int MatchExact(IReadOnlyList<TransactionRecord> bankTransactions, IReadOnlyList<TransactionRecord> r365Transactions, GroupIdGenerator groupIds)
    {
        var unmatchedR365 = r365Transactions.Where(r => !r.IsMatched);
        var index = IndexByAmount(unmatchedR365);
        int matchCount = 0;

        foreach (var bank in bankTransactions)
        {
            if (bank.IsMatched || bank.AmountCents == 0) continue;
            if (!index.TryGetValue(bank.AmountCents, out var candidates)) continue;

            TransactionRecord? chosen = null;
            foreach (var r in candidates)
            {
                if (r.IsMatched) continue;
                if (r.Date != bank.Date) continue;
                chosen = r; // list is FIFO-sorted, so first hit is FIFO-preferred
                break;
            }
            if (chosen is null) continue;

            LockPair(bank, chosen, MatchStatus.MatchedExact, "Matched (Exact)", ConfidenceScorer.ExactMatchScore, groupIds.Next());
            matchCount++;
        }
        return matchCount;
    }

    /// <summary>
    /// Exact amount, date within [bankDate - maxDays, bankDate], one-to-one
    /// (backward only — R365 is never allowed to be newer than the bank
    /// date). Only ever looks at transactions <see cref="MatchExact"/> left
    /// unmatched.
    /// </summary>
    public static int MatchDateTolerant(IReadOnlyList<TransactionRecord> bankTransactions, IReadOnlyList<TransactionRecord> r365Transactions, int maxDateDifferenceDays, GroupIdGenerator groupIds)
    {
        var unmatchedR365 = r365Transactions.Where(r => !r.IsMatched);
        var index = IndexByAmount(unmatchedR365);
        int matchCount = 0;

        foreach (var bank in bankTransactions)
        {
            if (bank.IsMatched || bank.AmountCents == 0) continue;
            if (!index.TryGetValue(bank.AmountCents, out var candidates)) continue;

            SelectionKey? bestKey = null;
            TransactionRecord? best = null;
            int bestDiff = 0;
            int bestConfidence = 0;

            foreach (var r in candidates)
            {
                if (r.IsMatched) continue;
                var diff = (bank.Date - r.Date).Days;
                if (diff < 0 || diff > maxDateDifferenceDays) continue;

                var confidence = ConfidenceScorer.OneToOneDateTolerant(diff, maxDateDifferenceDays);
                var key = new SelectionKey(diff == 0 ? 0 : 1, 1, r.Date, diff, -confidence);
                if (bestKey is null || key.CompareTo(bestKey.Value) < 0)
                {
                    bestKey = key;
                    best = r;
                    bestDiff = diff;
                    bestConfidence = confidence;
                }
            }
            if (best is null) continue;

            var comment = bestDiff == 0
                ? "Matched (Exact)"
                : $"Matched (Date Difference {bestDiff} Day{(bestDiff == 1 ? "" : "s")})";
            LockPair(bank, best, MatchStatus.MatchedDateTolerant, comment, bestConfidence, groupIds.Next());
            matchCount++;
        }
        return matchCount;
    }

    private static void LockPair(TransactionRecord bank, TransactionRecord r365, MatchStatus status, string comment, int confidence, int groupId)
    {
        bank.IsMatched = true;
        bank.Status = status;
        bank.Comment = comment;
        bank.ConfidenceScore = confidence;
        bank.GroupId = groupId;

        r365.IsMatched = true;
        r365.Status = status;
        r365.Comment = comment;
        r365.ConfidenceScore = confidence;
        r365.GroupId = groupId;
    }
}
