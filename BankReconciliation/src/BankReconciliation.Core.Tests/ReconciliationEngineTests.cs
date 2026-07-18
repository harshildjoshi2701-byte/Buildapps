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
        // one at the same amount. Pass 1 must claim the exact one, leaving
        // the older one for something else (here: nothing, so it's NoMatch).
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
        // exact match (Pass 1/2 always run before Pass 3, and once matched a
        // transaction is locked out of every later pass).
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
