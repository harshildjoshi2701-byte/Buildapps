using BankReconciliation.Core.Matching;
using BankReconciliation.Core.Models;
using Xunit;
using static BankReconciliation.Core.Tests.TestHelpers;

namespace BankReconciliation.Core.Tests;

public class OneToOneMatcherTests
{
    [Fact]
    public void MatchExact_SameDateSameAmount_Matches()
    {
        var bank = new[] { Bank(row: 3, dayOffset: 10, amountDollars: -500.00m) };
        var r365 = new[] { R365(row: 3, dayOffset: 10, amountDollars: -500.00m, grouped: false) };

        var count = OneToOneMatcher.MatchExact(bank, r365, new GroupIdGenerator());

        Assert.Equal(1, count);
        Assert.True(bank[0].IsMatched);
        Assert.True(r365[0].IsMatched);
        Assert.Equal(MatchStatus.MatchedExact, bank[0].Status);
        Assert.Equal("Matched (Exact)", bank[0].Comment);
        Assert.Equal(100, bank[0].ConfidenceScore);
        Assert.Equal(bank[0].GroupId, r365[0].GroupId);
    }

    [Fact]
    public void MatchExact_DifferentDate_DoesNotMatch()
    {
        var bank = new[] { Bank(3, 10, -500.00m) };
        var r365 = new[] { R365(3, 9, -500.00m, grouped: false) };

        var count = OneToOneMatcher.MatchExact(bank, r365, new GroupIdGenerator());

        Assert.Equal(0, count);
        Assert.False(bank[0].IsMatched);
    }

    [Fact]
    public void MatchExact_CreditNeverMatchesDebitOfSameMagnitude()
    {
        var bank = new[] { Bank(3, 10, 500.00m) };   // credit
        var r365 = new[] { R365(3, 10, -500.00m, grouped: false) }; // debit, same day, same magnitude

        var count = OneToOneMatcher.MatchExact(bank, r365, new GroupIdGenerator());

        Assert.Equal(0, count);
    }

    [Fact]
    public void MatchDateTolerant_WithinWindow_MatchesWithReducedConfidence()
    {
        var bank = new[] { Bank(3, 10, -1250.50m) };
        var r365 = new[] { R365(3, 7, -1250.50m, grouped: false) }; // 3 days older, within default 6-day window

        var settings = DefaultSettings();
        OneToOneMatcher.MatchExact(bank, r365, new GroupIdGenerator()); // no exact match
        var count = OneToOneMatcher.MatchDateTolerant(bank, r365, settings.MaxDateDifferenceDays, new GroupIdGenerator());

        Assert.Equal(1, count);
        Assert.Equal(MatchStatus.MatchedDateTolerant, bank[0].Status);
        Assert.Equal("Matched (Date Difference 3 Days)", bank[0].Comment);
        Assert.True(bank[0].ConfidenceScore < 100);
        Assert.True(bank[0].ConfidenceScore >= 90);
    }

    [Fact]
    public void MatchDateTolerant_R365NewerThanBank_NeverMatches()
    {
        // Spec: R365 date is always same-day-or-older than the bank date, never newer.
        var bank = new[] { Bank(3, 10, -800.00m) };
        var r365 = new[] { R365(3, 12, -800.00m, grouped: false) }; // 2 days NEWER

        var settings = DefaultSettings();
        var count = OneToOneMatcher.MatchDateTolerant(bank, r365, settings.MaxDateDifferenceDays, new GroupIdGenerator());

        Assert.Equal(0, count);
    }

    [Fact]
    public void MatchDateTolerant_BeyondMaxWindow_DoesNotMatch()
    {
        var bank = new[] { Bank(3, 10, -800.00m) };
        var r365 = new[] { R365(3, 3, -800.00m, grouped: false) }; // 7 days older; default window is 0-6

        var count = OneToOneMatcher.MatchDateTolerant(bank, r365, 6, new GroupIdGenerator());

        Assert.Equal(0, count);
    }

    [Fact]
    public void MatchDateTolerant_PrefersOldestFifoAmongEqualDateDiffCandidates()
    {
        // Two R365 candidates at the same date-difference: FIFO (oldest, then lowest row) wins.
        var bank = new[] { Bank(10, 10, -300.00m) };
        var older = R365(5, 8, -300.00m, grouped: false);
        var newer = R365(6, 8, -300.00m, grouped: false); // same date as `older`, higher row number

        var r365 = new[] { newer, older };
        var count = OneToOneMatcher.MatchDateTolerant(bank, r365, 6, new GroupIdGenerator());

        Assert.Equal(1, count);
        Assert.True(older.IsMatched);
        Assert.False(newer.IsMatched);
    }

    [Fact]
    public void LockedTransactions_AreNeverReusedAcrossPasses()
    {
        // Two bank transactions, identical amount; only one R365 candidate exists.
        var bank = new[] { Bank(3, 10, -100.00m), Bank(4, 10, -100.00m) };
        var r365 = new[] { R365(3, 10, -100.00m, grouped: false) };

        var count = OneToOneMatcher.MatchExact(bank, r365, new GroupIdGenerator());

        Assert.Equal(1, count);
        Assert.NotEqual(bank[0].IsMatched, bank[1].IsMatched); // exactly one of the two got it, not both
    }

    [Fact]
    public void StrictOneToOne_NeverCombinesNonR365Rows()
    {
        // Rule 2: rows without the group keyword must never be combined, even
        // if two of them would sum exactly to a bank transaction.
        var bank = new[] { Bank(3, 10, -300.00m) };
        var r365 = new[]
        {
            R365(3, 10, -150.00m, grouped: false),
            R365(4, 10, -150.00m, grouped: false),
        };

        OneToOneMatcher.MatchExact(bank, r365, new GroupIdGenerator());
        OneToOneMatcher.MatchDateTolerant(bank, r365, 6, new GroupIdGenerator());

        Assert.False(bank[0].IsMatched);
    }
}
