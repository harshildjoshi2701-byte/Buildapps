using BankReconciliation.Core.Matching;
using BankReconciliation.Core.Models;
using Xunit;
using static BankReconciliation.Core.Tests.TestHelpers;

namespace BankReconciliation.Core.Tests;

public class ReconciliationEngineTests
{
    [Fact]
    public async Task RunAsync_ExactMatchPreferredOverDateTolerantMatch()
    {
        // Bank txn could match either an exact-date R365 row or a 2-day-older
        // one at the same amount. Exact matching must claim the exact one
        // first, leaving the older one for something else (here: nothing, so
        // it's NoMatch).
        var bank = new[] { Bank(1, 10, -500.00m) };
        var r365 = new[]
        {
            R365(1, 10, -500.00m, grouped: false), // exact date
            R365(2, 8, -500.00m, grouped: false),   // 2 days older
        };

        var engine = new ReconciliationEngine();
        var result = await engine.RunAsync(bank, r365, DefaultSettings());

        Assert.Equal("Matched (Exact)", bank[0].Comment);
        Assert.True(r365[0].IsMatched);
        Assert.False(r365[1].IsMatched);
        Assert.Equal(1, result.Summary.OneToOneMatches);
    }

    [Fact]
    public async Task RunAsync_OneToOnePassesRunBeforeCombinationPass()
    {
        // A bank transaction that could EITHER match a single R365 row
        // exactly OR be explained by a combination must take the single
        // exact match (exact and date-tolerant matching always run before
        // combination search within a partition, and once matched a
        // transaction is locked out of every later stage).
        var bank = new[] { Bank(1, 10, -100.00m) };
        var r365 = new[]
        {
            R365(1, 10, -100.00m),               // exact single match
            R365(2, 9, -60.00m),                  // would-be combination member
            R365(3, 9, -40.00m),                  // would-be combination member
        };

        var engine = new ReconciliationEngine();
        var result = await engine.RunAsync(bank, r365, DefaultSettings());

        Assert.Equal(MatchStatus.MatchedExact, bank[0].Status);
        Assert.False(r365[1].IsMatched);
        Assert.False(r365[2].IsMatched);
        Assert.Equal(0, result.Summary.CombinationMatches);
    }

    [Fact]
    public async Task RunAsync_FullPipeline_ProducesConsistentSummaryCounts()
    {
        var bank = new List<TransactionRecord>
        {
            Bank(1, 10, -500.00m),   // exact match
            Bank(2, 10, -300.00m),   // date-tolerant match (3 days)
            Bank(3, 10, -180.00m),   // combination match (2 items)
            Bank(4, 10, -999.99m),   // no match at all
        };
        var r365 = new List<TransactionRecord>
        {
            R365(10, 10, -500.00m, grouped: false),
            R365(11, 7, -300.00m, grouped: false),
            R365(12, 9, -100.00m),
            R365(13, 9, -80.00m),
        };

        var engine = new ReconciliationEngine();
        var result = await engine.RunAsync(bank, r365, DefaultSettings());
        var summary = result.Summary;

        Assert.Equal(4, summary.TotalBankTransactions);
        Assert.Equal(4, summary.TotalR365Transactions);
        Assert.Equal(3, summary.MatchedBankTransactions);
        Assert.Equal(4, summary.MatchedR365Transactions);
        Assert.Equal(1, summary.UnmatchedBankTransactions);
        Assert.Equal(2, summary.OneToOneMatches); // one exact + one date-tolerant
        Assert.Equal(1, summary.CombinationMatches);
        Assert.Equal(3, result.MatchGroups.Count);

        // Every match group's members must sum exactly to the bank amount.
        foreach (var group in result.MatchGroups)
        {
            var sum = group.R365Transactions.Sum(r => r.AmountCents);
            Assert.Equal(group.BankTransaction.AmountCents, sum);
        }
    }

    [Fact]
    public async Task RunAsync_OneSidedGroupingBucket_CountsAsUnmatchedOnlyNeverAlsoMatched()
    {
        // Regression test for a real production bug: a whole sheet's worth of
        // rows sharing one non-blank Grouping value with nothing on the other
        // sheet (e.g. every bank row on a single-account statement carrying
        // the same "Account Name" text in a misconfigured Grouping column)
        // must be reported as unmatched, never as BOTH matched and unmatched
        // at once. All 50 rows share one "AP ACCOUNT" Grouping partition (see
        // GroupingPartitioner) that is one-sided — nothing on the R365 side
        // to compare against — so exact/date-tolerant/combination matching
        // within the partition finds nothing, and FlagUnresolvedResidual is a
        // no-op (it only fires when BOTH sides have leftovers) — every row
        // falls through untouched to normal No Match finalization.
        var bank = new List<TransactionRecord>();
        for (int i = 0; i < 50; i++)
            bank.Add(Bank(i + 1, 0, 100.00m + i, groupingKey: "AP ACCOUNT"));
        var r365 = new List<TransactionRecord>(); // nothing on the other side at all

        var engine = new ReconciliationEngine();
        var result = await engine.RunAsync(bank, r365, DefaultSettings());
        var summary = result.Summary;

        Assert.Equal(50, summary.TotalBankTransactions);
        Assert.Equal(0, summary.MatchedBankTransactions);
        Assert.Equal(50, summary.UnmatchedBankTransactions);
        Assert.All(bank, b => Assert.Equal(MatchStatus.NoMatch, b.Status));
        Assert.All(bank, b => Assert.False(b.IsMatched)); // genuinely not a match, not just locked
        Assert.Empty(result.MatchGroups); // no genuine match to display as a group
    }

    [Fact]
    public async Task RunAsync_LopsidedVendorTagGroup_FindsGenuineMatchesAndNeverBundlesLeftoversIntoOneReviewBlob()
    {
        // Regression test built directly from validating the fix against the
        // user's real workbook: a "Sysco"-shaped Grouping value with 2,541
        // Bank rows against 100 R365 rows, where only a handful of amounts
        // are shared between the two sides at all. Reproduced here at a
        // smaller but still lopsided scale. Two things must both hold:
        //   1. The few genuine matches within the group are found despite
        //      the group's massive size/imbalance (the actual production
        //      bug — GroupingMatcher's bucket-sum-and-lock design gave up on
        //      the entire group the moment its totals didn't tie out).
        //   2. The hundreds of leftover rows that share nothing with
        //      anything else do NOT get bundled into one giant Manual Review
        //      comment (an earlier version of this fix's own
        //      FlagUnresolvedResidual step did exactly that — see its
        //      MaxResidualRowsToFlag remarks).
        var bank = new List<TransactionRecord>();
        for (int i = 0; i < 500; i++)
            bank.Add(Bank(i + 1, 0, 1000.00m + i, groupingKey: "VendorX")); // $1000.00-$1499.00, all distinct
        var r365 = new List<TransactionRecord>();
        for (int i = 0; i < 15; i++)
            r365.Add(R365(i + 1, 0, 9000.00m + i, groupingKey: "VendorX")); // $9000.00-$9014.00 — disjoint range
        // Seed exactly 5 genuine matches by overwriting 5 R365 amounts to
        // land exactly on 5 of the Bank amounts above (same day, so exact match applies).
        r365[0] = R365(101, 0, 1010.00m, groupingKey: "VendorX");
        r365[1] = R365(102, 0, 1050.00m, groupingKey: "VendorX");
        r365[2] = R365(103, 0, 1123.00m, groupingKey: "VendorX");
        r365[3] = R365(104, 0, 1250.00m, groupingKey: "VendorX");
        r365[4] = R365(105, 0, 1499.00m, groupingKey: "VendorX");

        var engine = new ReconciliationEngine();
        var result = await engine.RunAsync(bank, r365, DefaultSettings());

        var matchedBank = bank.Where(b => b.Status == MatchStatus.MatchedExact).ToList();
        Assert.Equal(5, matchedBank.Count);
        Assert.Equal(new[] { 1010.00m, 1050.00m, 1123.00m, 1250.00m, 1499.00m }, matchedBank.Select(b => b.AmountDollars).OrderBy(a => a));

        // The remaining 495 Bank / 10 R365 rows have nothing to match and
        // must be plain No Match, individually — never Manual Review, and
        // critically never sharing one comment across hundreds of rows.
        var unmatchedBank = bank.Where(b => b.Status != MatchStatus.MatchedExact).ToList();
        Assert.Equal(495, unmatchedBank.Count);
        Assert.All(unmatchedBank, b => Assert.Equal(MatchStatus.NoMatch, b.Status));
        Assert.All(unmatchedBank, b => Assert.Contains("VendorX", b.Comment)); // Grouping context preserved per row
        Assert.DoesNotContain(bank, b => b.Status == MatchStatus.ManualReview);
        Assert.DoesNotContain(r365, r => r.Status == MatchStatus.ManualReview);
    }

    [Fact]
    public async Task RunAsync_NoTransactionIsEverDoubleCounted()
    {
        // Property check across a larger synthetic set: every R365 row
        // belongs to at most one match group, every bank row is matched at
        // most once.
        var rnd = new Random(7);
        var bank = new List<TransactionRecord>();
        var r365 = new List<TransactionRecord>();
        int row = 1;
        for (int i = 0; i < 60; i++)
        {
            var amt = rnd.Next(500, 500000) / 100m * (rnd.Next(2) == 0 ? 1 : -1);
            bank.Add(Bank(row++, rnd.Next(0, 15), amt));
        }
        row = 1000;
        for (int i = 0; i < 80; i++)
        {
            var amt = rnd.Next(500, 500000) / 100m * (rnd.Next(2) == 0 ? 1 : -1);
            r365.Add(R365(row++, rnd.Next(0, 15), amt, grouped: rnd.Next(2) == 0));
        }

        var engine = new ReconciliationEngine();
        var settings = DefaultSettings();
        settings.GlobalCombinationTimeBudgetSeconds = 10.0;
        var result = await engine.RunAsync(bank, r365, settings);

        var allGroupedR365Ids = result.MatchGroups.SelectMany(g => g.R365Transactions).Select(r => r.RowNumber).ToList();
        Assert.Equal(allGroupedR365Ids.Count, allGroupedR365Ids.Distinct().Count());

        var allBankIds = result.MatchGroups.Select(g => g.BankTransaction.RowNumber).ToList();
        Assert.Equal(allBankIds.Count, allBankIds.Distinct().Count());

        // Every reported match must genuinely sum correctly — the one
        // invariant that must never break regardless of what the random data
        // happened to generate.
        foreach (var group in result.MatchGroups)
            Assert.Equal(group.BankTransaction.AmountCents, group.R365Transactions.Sum(r => r.AmountCents));
    }

    [Fact]
    public async Task RunAsync_EveryTransactionEndsInATerminalStatus()
    {
        var bank = new[] { Bank(1, 10, -75.00m), Bank(2, 10, -75.00m) }; // duplicates, no R365 counterpart
        var r365 = Array.Empty<TransactionRecord>();

        var engine = new ReconciliationEngine();
        var result = await engine.RunAsync(bank, r365, DefaultSettings());

        Assert.All(bank, b => Assert.NotEqual(MatchStatus.Unmatched, b.Status));
        Assert.All(bank, b => Assert.Equal(MatchStatus.PossibleDuplicate, b.Status));
    }

    [Fact]
    public async Task RunAsync_CancellationIsRespected()
    {
        var bank = Enumerable.Range(1, 500).Select(i => Bank(i, i % 10, -(i * 3.33m))).ToList();
        var r365 = Enumerable.Range(1, 500).Select(i => R365(i + 1000, i % 10, -(i * 3.33m))).ToList();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var engine = new ReconciliationEngine();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => engine.RunAsync(bank, r365, DefaultSettings(), progress: null, cts.Token));
    }
}
