using BankReconciliation.Core.Models;

namespace BankReconciliation.Core.Matching;

/// <summary>
/// Implements the Grouping-column rule as a SEARCH-SPACE PARTITION, not a
/// match type of its own. A non-blank Grouping value is an assertion that
/// two rows are only ever comparable if they share it — rows in different
/// Grouping partitions (including the blank one) must NEVER be compared to
/// each other, full stop. What happens WITHIN a partition is still the
/// normal staged pipeline (exact -> date-tolerant -> bounded combination
/// search), just scoped to that partition's own rows.
///
/// This deliberately replaces an earlier design (the retired
/// <c>GroupingMatcher</c>) that summed an entire Grouping bucket on each
/// side and compared the two totals as one all-or-nothing unit, permanently
/// locking every row in the bucket regardless of outcome. That worked for a
/// true linking ID shared by a handful of rows that genuinely sum together,
/// but broke completely for a Grouping value that is really a vendor or
/// category tag (e.g. "Sysco") shared by thousands of otherwise-unrelated
/// transactions on one side and a much smaller number on the other — the
/// bucket total was never going to tie out, so every one of those rows was
/// dumped into Manual Review (or, worse, silently mis-locked) even though
/// most of them had a perfectly good individual match sitting right there
/// within the same partition. Partitioning first and then running the same
/// real matchers used everywhere else finds those individual matches
/// naturally, and still finds a "clean" linking-ID group's full-bucket tie
/// via combination search — it's a strict generalization, not a special
/// case bolted on top.
/// </summary>
public static class GroupingPartitioner
{
    /// <summary>Key used for the single partition holding every row with a
    /// blank/whitespace Grouping value — normal, ungrouped matching, exactly
    /// as if the Grouping column didn't exist for these rows.</summary>
    public const string UngroupedKey = "";

    public sealed class Partition
    {
        public required string Key { get; init; }

        /// <summary>False only for the single ungrouped partition.</summary>
        public required bool IsGrouped { get; init; }

        public required List<TransactionRecord> Bank { get; init; }
        public required List<TransactionRecord> R365 { get; init; }

        public int RowCount => Bank.Count + R365.Count;
    }

    /// <summary>Splits every not-yet-matched row from both sides into
    /// partitions by Grouping value (case-insensitive, trimmed), plus one
    /// partition for blank values. Already-matched rows (pre-locked, or
    /// claimed by an earlier named-rule pass) are excluded entirely — they
    /// have nothing left to do in any partition. Grouped partitions are
    /// returned in a stable, deterministic order (ordinal-ignore-case on the
    /// key) so runs and logs are reproducible; the ungrouped partition is
    /// always last.</summary>
    public static List<Partition> BuildPartitions(IReadOnlyList<TransactionRecord> bank, IReadOnlyList<TransactionRecord> r365)
    {
        var bankBuckets = Bucket(bank);
        var r365Buckets = Bucket(r365);

        var groupedKeys = new HashSet<string>(bankBuckets.Keys, StringComparer.OrdinalIgnoreCase);
        groupedKeys.UnionWith(r365Buckets.Keys);
        groupedKeys.Remove(UngroupedKey);

        var partitions = new List<Partition>();
        foreach (var key in groupedKeys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            bankBuckets.TryGetValue(key, out var b);
            r365Buckets.TryGetValue(key, out var r);
            partitions.Add(new Partition { Key = key, IsGrouped = true, Bank = b ?? new List<TransactionRecord>(), R365 = r ?? new List<TransactionRecord>() });
        }

        bankBuckets.TryGetValue(UngroupedKey, out var ungroupedBank);
        r365Buckets.TryGetValue(UngroupedKey, out var ungroupedR365);
        partitions.Add(new Partition
        {
            Key = UngroupedKey,
            IsGrouped = false,
            Bank = ungroupedBank ?? new List<TransactionRecord>(),
            R365 = ungroupedR365 ?? new List<TransactionRecord>(),
        });

        return partitions;
    }

    private static Dictionary<string, List<TransactionRecord>> Bucket(IReadOnlyList<TransactionRecord> transactions)
    {
        var buckets = new Dictionary<string, List<TransactionRecord>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in transactions)
        {
            if (t.IsMatched) continue;
            var key = string.IsNullOrWhiteSpace(t.GroupingKey) ? UngroupedKey : t.GroupingKey.Trim();
            if (!buckets.TryGetValue(key, out var list)) { list = new List<TransactionRecord>(); buckets[key] = list; }
            list.Add(t);
        }
        return buckets;
    }

    /// <summary>
    /// Rule: a Grouping value is not always a reconciliation ID — it can be a
    /// vendor/category tag. Do not permanently lock rows in a group just
    /// because the group's totals differ; let the normal exact/date-tolerant/
    /// combination matchers have a real try first (the caller already ran
    /// them on this partition by the time this is called). Only once that's
    /// exhausted, if BOTH sides still have unmatched rows in this partition,
    /// flag the leftovers as Manual Review — there IS a plausible
    /// relationship (they share the Grouping value) that the numbers didn't
    /// confirm, which is worth a human's attention. A leftover on only ONE
    /// side has no counterpart to flag against at all and is left for the
    /// standard No Match / Possible Duplicate finalization instead.
    ///
    /// BOUNDED to a small residual (see <see cref="MaxResidualRowsToFlag"/>):
    /// "Manual Review" means a human can open the file and look at every row
    /// it names. A vendor tag shared by thousands of rows on one side and a
    /// couple hundred on the other (the real scenario this design replaces
    /// GroupingMatcher's bucket-sum-and-lock for) can easily leave hundreds
    /// of genuinely-unrelated rows unmatched after real matching has already
    /// claimed everything it could — bundling all of them into one "Manual
    /// Review" comment would just be the original bug wearing a different
    /// status label. Past the threshold, leftovers fall through to the
    /// normal No Match / Possible Duplicate finalization instead, which still
    /// carries the Grouping value in each row's own comment (see
    /// <see cref="DuplicateDetector"/>) — reviewable per row, sortable/
    /// filterable by Grouping in Excel, without one unreadable mega-comment.
    ///
    /// Deliberately does NOT set <see cref="TransactionRecord.IsMatched"/> or
    /// a real GroupId — this is not a match, so it must not be counted or
    /// displayed as one (see <see cref="ReconciliationEngine"/>'s
    /// IsGenuinelyMatched remarks). Setting Status away from Unmatched is
    /// enough to keep <see cref="DuplicateDetector"/> from re-touching it.
    /// </summary>
    public static void FlagUnresolvedResidual(Partition partition)
    {
        if (!partition.IsGrouped) return;

        var leftoverBank = partition.Bank.Where(b => !b.IsMatched).ToList();
        var leftoverR365 = partition.R365.Where(r => !r.IsMatched).ToList();
        if (leftoverBank.Count == 0 || leftoverR365.Count == 0) return;
        if (leftoverBank.Count + leftoverR365.Count > MaxResidualRowsToFlag) return;

        var bankResidual = leftoverBank.Sum(b => b.AmountCents);
        var r365Residual = leftoverR365.Sum(r => r.AmountCents);
        var comment = $"Manual Review (Grouping \"{partition.Key}\" — {leftoverBank.Count} Bank / {leftoverR365.Count} R365 transaction(s) still unmatched after exact/date/combination matching within this group; residual Bank {MoneyMath.ToDollars(bankResidual):C} vs R365 {MoneyMath.ToDollars(r365Residual):C})";

        foreach (var t in leftoverBank)
        {
            t.Status = MatchStatus.ManualReview;
            t.Comment = comment;
        }
        foreach (var t in leftoverR365)
        {
            t.Status = MatchStatus.ManualReview;
            t.Comment = comment;
        }
    }

    /// <summary>Above this many combined leftover rows, a single shared
    /// "Manual Review" comment stops being something a human can actually
    /// review and starts being noise — see <see cref="FlagUnresolvedResidual"/>
    /// remarks. Deliberately a small constant, not a Settings knob: there is
    /// no reasonable file-specific tuning for "how many rows can a person
    /// look at in one sitting."</summary>
    private const int MaxResidualRowsToFlag = 20;
}
