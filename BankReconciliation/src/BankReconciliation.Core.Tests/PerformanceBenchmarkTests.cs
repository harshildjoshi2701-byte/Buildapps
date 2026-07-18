using System.Diagnostics;
using BankReconciliation.Core.Matching;
using BankReconciliation.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace BankReconciliation.Core.Tests;

/// <summary>
/// Not a correctness test — a real timing measurement against the spec's
/// stated scale (2,000-10,000 transactions, "must remain responsive"). Every
/// performance claim in the README before this was based on an old Python
/// prototype run against different data; this runs the ACTUAL compiled
/// C# engine and reports real numbers. The pass/fail bound is deliberately
/// generous (this container is a shared, throttled cloud sandbox, almost
/// certainly slower than a real user's Windows desktop) — the point is
/// catching a genuine performance regression, not chasing a target time.
/// </summary>
public class PerformanceBenchmarkTests
{
    private readonly ITestOutputHelper _output;

    public PerformanceBenchmarkTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task RunAsync_TenThousandTransactions_CompletesWithinAGenerousBoundAndReportsRealTiming()
    {
        var (bank, r365) = GenerateRealisticDataset(bankCount: 5_000, r365Count: 5_000);
        var settings = new ReconciliationSettings(); // real shipped defaults, not test-tuned ones

        var sw = Stopwatch.StartNew();
        var result = await new ReconciliationEngine().RunAsync(bank, r365, settings);
        sw.Stop();

        _output.WriteLine($"{bank.Count + r365.Count:N0} transactions ({bank.Count:N0} bank / {r365.Count:N0} R365) reconciled in {sw.Elapsed.TotalSeconds:F2}s.");
        _output.WriteLine($"Bank matched: {result.Summary.MatchedBankTransactions:N0}/{result.Summary.TotalBankTransactions:N0} ({result.Summary.BankMatchPercentage:F1}%).");
        _output.WriteLine($"R365 matched: {result.Summary.MatchedR365Transactions:N0}/{result.Summary.TotalR365Transactions:N0} ({result.Summary.R365MatchPercentage:F1}%).");
        _output.WriteLine($"One-to-one: {result.Summary.OneToOneMatches:N0}, Grouping: {result.Summary.GroupingMatches:N0}, Combination: {result.Summary.CombinationMatches:N0}, Manual review: {result.Summary.ManualReviewCount:N0}.");

        // Generous on purpose — see class remarks. A real regression (e.g. an
        // accidental O(n^2) somewhere) would blow well past this, not sit
        // just over it.
        Assert.True(sw.Elapsed.TotalSeconds < 90,
            $"Expected under 90s even on a throttled shared sandbox; took {sw.Elapsed.TotalSeconds:F2}s.");

        // Every transaction must still end in a terminal status regardless of scale.
        Assert.All(bank, t => Assert.NotEqual(MatchStatus.Unmatched, t.Status));
        Assert.All(r365, t => Assert.NotEqual(MatchStatus.Unmatched, t.Status));
    }

    /// <summary>Builds a mixed dataset shaped like the real workbook: most
    /// rows are plain exact or date-tolerant matches, a slice carries shared
    /// Grouping IDs (2-6 R365 rows per bank row), a slice needs genuine
    /// combination search, and a slice is deliberately unmatched — so the
    /// benchmark exercises every stage, not just the cheap ones.
    ///
    /// Each category's amount magnitude is drawn from a DISJOINT band
    /// ($20k-$100k plain, $10-$300 grouped members, $1k-$4k combination
    /// members). The plain and combination categories both land in the same
    /// blank-Grouping partition and could still collide with each other by
    /// amount if their ranges overlapped — an earlier version shared ranges
    /// and individual combination-search members kept coincidentally
    /// colliding with unrelated plain-category exact-match targets, so exact
    /// matching (which runs before combination search within any partition)
    /// silently cannibalized them before combination search ever got a
    /// chance, masking that code path from this benchmark entirely. (The
    /// grouped category doesn't need a disjoint band for this reason — each
    /// grouped bank row gets its own unique Grouping key, hence its own
    /// partition, so it can never collide with plain or combination rows
    /// regardless of amount; it keeps a distinct band anyway for clarity when
    /// reading a failure.) Dates spread across a full year rather than a
    /// narrow window, too, so same-window candidate pools stay well under
    /// MaxCombinationPoolSize (600) and don't trigger the "pool truncated,
    /// don't guess" safety valve.</summary>
    private static (List<TransactionRecord> Bank, List<TransactionRecord> R365) GenerateRealisticDataset(int bankCount, int r365Count)
    {
        var rnd = new Random(12345); // fixed seed: reproducible timing runs
        var baseDate = new DateTime(2026, 1, 1);
        var bank = new List<TransactionRecord>();
        var r365 = new List<TransactionRecord>();
        int bankRow = 1, r365Row = 1;

        int groupedBankCount = bankCount / 10;      // 10% Grouping-linked
        int combinationBankCount = bankCount / 10;   // 10% need real combination search
        int unmatchedBankCount = bankCount / 20;     // 5% genuinely unmatched
        int plainBankCount = bankCount - groupedBankCount - combinationBankCount - unmatchedBankCount;

        DateTime RandomDate() => baseDate.AddDays(rnd.Next(0, 365));

        // Plain exact / date-tolerant matches. Band: $20,000-$100,000.
        for (int i = 0; i < plainBankCount; i++)
        {
            var amountCents = (long)rnd.Next(2_000_000, 10_000_000) * (rnd.Next(2) == 0 ? 1 : -1);
            var bankDate = RandomDate();
            var dateOffset = rnd.Next(0, 2) == 0 ? 0 : -rnd.Next(1, 6); // exact or date-tolerant
            bank.Add(new TransactionRecord { RowNumber = bankRow++, Side = TransactionSide.Bank, Date = bankDate, AmountCents = amountCents });
            r365.Add(new TransactionRecord { RowNumber = r365Row++, Side = TransactionSide.R365, Date = bankDate.AddDays(dateOffset), AmountCents = amountCents });
        }

        // Grouping-linked: one bank row summed against 2-6 R365 rows sharing
        // a Grouping ID. Band: $10-$300 per member. Safe from cross-category
        // collision regardless of range overlap, since each bank row here
        // gets its own unique Grouping key and therefore its own partition —
        // see GroupingPartitioner remarks.
        for (int i = 0; i < groupedBankCount; i++)
        {
            var key = $"G{i}";
            var memberCount = rnd.Next(2, 7);
            long total = 0;
            var bankDate = RandomDate();
            for (int m = 0; m < memberCount; m++)
            {
                var amt = (long)rnd.Next(1_000, 30_000);
                total += amt;
                r365.Add(new TransactionRecord { RowNumber = r365Row++, Side = TransactionSide.R365, Date = bankDate.AddDays(-rnd.Next(0, 5)), AmountCents = amt, GroupingKey = key });
            }
            bank.Add(new TransactionRecord { RowNumber = bankRow++, Side = TransactionSide.Bank, Date = bankDate, AmountCents = total, GroupingKey = key });
        }

        // Needs real combination search: bank amount = sum of several R365
        // rows with NO shared Grouping key. Band: $1,000-$4,000 per member —
        // disjoint from both the plain and grouped bands above.
        for (int i = 0; i < combinationBankCount; i++)
        {
            var memberCount = rnd.Next(2, 5);
            long total = 0;
            var bankDate = RandomDate();
            for (int m = 0; m < memberCount; m++)
            {
                var amt = (long)rnd.Next(100_000, 400_000);
                total += amt;
                r365.Add(new TransactionRecord { RowNumber = r365Row++, Side = TransactionSide.R365, Date = bankDate.AddDays(-rnd.Next(0, 6)), AmountCents = amt });
            }
            bank.Add(new TransactionRecord { RowNumber = bankRow++, Side = TransactionSide.Bank, Date = bankDate, AmountCents = total });
        }

        // Genuinely unmatched bank rows (no corresponding R365 amount at all).
        for (int i = 0; i < unmatchedBankCount; i++)
        {
            bank.Add(new TransactionRecord { RowNumber = bankRow++, Side = TransactionSide.Bank, Date = RandomDate(), AmountCents = 987_654_321 + i });
        }

        // NOTE: r365Count is a target, not exact — the grouped/combination
        // categories add a variable number of members per bank row, so the
        // real total lands somewhere near it. An earlier version forced an
        // exact count via .Take(r365Count), which silently truncated the
        // list in insertion order — since plain + grouped rows alone already
        // exceeded the target before any combination rows were even added,
        // that truncation was discarding the entire combination category,
        // and this benchmark was reporting a clean number without actually
        // exercising the combination-search code path at all. Top up with
        // unmatched noise (another disjoint band) only if genuinely short.
        while (r365.Count < r365Count)
        {
            r365.Add(new TransactionRecord { RowNumber = r365Row++, Side = TransactionSide.R365, Date = RandomDate(), AmountCents = 555_555_555 + r365.Count });
        }

        return (bank, r365);
    }
}
