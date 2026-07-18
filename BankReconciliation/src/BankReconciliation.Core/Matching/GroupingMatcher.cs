using BankReconciliation.Core.Models;

namespace BankReconciliation.Core.Matching;

/// <summary>
/// Implements the new Grouping-column rule: when both sheets carry a
/// "Grouping" value, every row sharing that exact value on the Bank side is
/// summed, every row sharing it on the R365 side is summed, and the two
/// totals are compared — a hash-bucket operation, not a search. This is
/// fundamentally different from <see cref="CombinationMatcher"/>'s subset-sum
/// search: a populated Grouping value is an ASSERTION the workbook has
/// already made about which rows belong together, not a hint to go looking
/// for a combination. There is nothing to search for, so this pass is O(n)
/// regardless of how many rows share a Grouping value.
///
/// PASS ORDER — this runs AFTER <see cref="CombinationMatcher.RunSpecialComboRules"/>
/// (named rules) and BEFORE Pass 2 (exact one-to-one). That ordering is what
/// makes the Sysco/Grouping split correct with zero special-casing in this
/// class: the real workbook's Grouping column holds both true numeric linking
/// IDs (which genuinely sum-match across sheets) AND the keyword "Sysco"
/// (which never sum-matches — it's a curated, unconditional pairing, not a
/// financial total). Sysco rows are claimed and locked by the named-rule pass
/// FIRST, using the SAME Grouping column as a keyword-match column (see the
/// default <see cref="SpecialComboRule"/> in <see cref="ReconciliationSettings"/>).
/// By the time this class runs, those rows already have <c>IsMatched == true</c>
/// and are excluded by the standard <c>!IsMatched</c> filtering every matcher
/// in this codebase already applies — so this class only ever buckets
/// genuine linking-ID values, without needing to know "Sysco" is special.
/// (Running Grouping-key matching BEFORE named rules instead — arguably more
/// "obviously" correct, since an explicit ID is a stronger signal than a
/// curated keyword — was considered and rejected: this codebase's locking
/// invariant means the FIRST pass to touch a row decides its fate for every
/// later pass. If this class ran first and bucketed the literal string
/// "Sysco" as its own group, it would sum ALL ~2,500 Sysco bank rows against
/// ALL ~100 Sysco R365 rows, find they don't tie out — they never have, by
/// design — and permanently lock every one of those rows into Manual Review
/// before the named-rule pass ever got a chance to claim them correctly.)
///
/// EXCLUSIVITY — once a row has a non-blank Grouping value, it is resolved by
/// THIS mechanism only: matched as a group, flagged for review as a
/// non-tying group, or flagged as a one-sided group with no counterpart. It
/// never falls through to per-row exact/date-tolerant/combination matching
/// afterward, because doing so would silently break apart a linkage the
/// workbook explicitly asserted. Every row this class touches is locked
/// (<c>IsMatched = true</c>) regardless of outcome, unlike
/// <see cref="CombinationMatcher"/>'s soft-retry ManualReview (which exists
/// because a subset-sum search's candidate pool can genuinely shrink and
/// succeed on a later sweep). A Grouping bucket's membership is fixed by the
/// literal column value — retrying it later would recompute the exact same
/// sums and reach the exact same conclusion, so there is no benefit to
/// leaving it open, and real benefit (no double-claiming) to locking it now.
/// </summary>
public static class GroupingMatcher
{
    /// <summary>Runs the bucket-and-sum pass over every still-unmatched row
    /// with a non-blank Grouping value. Returns the number of buckets that
    /// tied out and were accepted as matches (not the number of rows).</summary>
    public static int MatchAll(
        IReadOnlyList<TransactionRecord> bankTransactions,
        IReadOnlyList<TransactionRecord> r365Transactions,
        ReconciliationSettings settings,
        GroupIdGenerator groupIds)
    {
        var bankBuckets = BucketByGroupingKey(bankTransactions);
        var r365Buckets = BucketByGroupingKey(r365Transactions);

        var allKeys = new HashSet<string>(bankBuckets.Keys, StringComparer.OrdinalIgnoreCase);
        allKeys.UnionWith(r365Buckets.Keys);

        int tiedOutCount = 0;
        foreach (var key in allKeys)
        {
            bankBuckets.TryGetValue(key, out var bankRows);
            r365Buckets.TryGetValue(key, out var r365Rows);
            bankRows ??= new List<TransactionRecord>();
            r365Rows ??= new List<TransactionRecord>();

            var allMembers = new List<TransactionRecord>(bankRows.Count + r365Rows.Count);
            allMembers.AddRange(bankRows);
            allMembers.AddRange(r365Rows);

            if (bankRows.Count == 0 || r365Rows.Count == 0)
            {
                // One-sided: nothing on the other sheet carries this Grouping
                // value at all. Locked (excluded from later per-row matching —
                // see class remarks on exclusivity) but deliberately NOT given
                // a real GroupId: a GroupId means "the engine paired this row
                // with something," which is untrue here. Giving these a real
                // id made the Excel Match ID column and the app's own Matched
                // count include rows explicitly labeled "No Match" — see
                // ReconciliationEngine's IsGenuinelyMatched.
                var side = bankRows.Count == 0 ? "R365" : "Bank";
                var totalCents = allMembers.Sum(m => m.AmountCents);
                var comment = $"No Match (Grouping \"{key}\" — {allMembers.Count} {side}-side transaction(s) totaling {MoneyMath.ToDollars(totalCents):C}, no corresponding Grouping value on the other sheet)";
                Lock(allMembers, MatchStatus.NoMatch, comment, confidence: 0, groupId: -1);
                continue;
            }

            var bankSum = bankRows.Sum(b => b.AmountCents);
            var r365Sum = r365Rows.Sum(r => r.AmountCents);

            if (MoneyMath.AmountsEqual(bankSum, r365Sum, settings.AmountToleranceDollars))
            {
                var groupId = groupIds.Next();
                var maxDateDiff = (int)(allMembers.Max(m => m.Date) - allMembers.Min(m => m.Date)).TotalDays;
                var confidence = ConfidenceScorer.Combination(allMembers.Count, maxDateDiff, settings.MaxDateDifferenceDays, ambiguous: false);
                var comment = $"Matched (Grouping \"{key}\", {allMembers.Count} Transactions)";
                Lock(allMembers, MatchStatus.MatchedCombination, comment, confidence, groupId);
                tiedOutCount++;
            }
            else
            {
                // Both sides present but totals don't tie out — needs a human
                // to look at it. Locked, like the one-sided case above, but
                // also NOT given a real GroupId: ManualReview is not a match.
                var diffDollars = MoneyMath.ToDollars(Math.Abs(bankSum - r365Sum));
                var comment = $"Manual Review (Grouping \"{key}\" — Bank total {MoneyMath.ToDollars(bankSum):C} vs R365 total {MoneyMath.ToDollars(r365Sum):C}, differs by {diffDollars:C})";
                Lock(allMembers, MatchStatus.ManualReview, comment, confidence: 0, groupId: -1);
            }
        }
        return tiedOutCount;
    }

    private static Dictionary<string, List<TransactionRecord>> BucketByGroupingKey(IReadOnlyList<TransactionRecord> transactions)
    {
        var buckets = new Dictionary<string, List<TransactionRecord>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in transactions)
        {
            if (t.IsMatched || string.IsNullOrWhiteSpace(t.GroupingKey)) continue;
            if (!buckets.TryGetValue(t.GroupingKey, out var list))
            {
                list = new List<TransactionRecord>();
                buckets[t.GroupingKey] = list;
            }
            list.Add(t);
        }
        return buckets;
    }

    private static void Lock(IEnumerable<TransactionRecord> members, MatchStatus status, string comment, int confidence, int groupId)
    {
        foreach (var m in members)
        {
            m.IsMatched = true;
            m.Status = status;
            m.Comment = comment;
            m.ConfidenceScore = confidence;
            m.GroupId = groupId;
        }
    }
}
