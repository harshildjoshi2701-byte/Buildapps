using BankReconciliation.Core.Matching;
using BankReconciliation.Core.Models;
using Xunit;
using static BankReconciliation.Core.Tests.TestHelpers;

namespace BankReconciliation.Core.Tests;

public class GroupingMatcherTests
{
    [Fact]
    public void MatchAll_ManyToOneTiesOutExactly_AllMembersMatched()
    {
        // The spec's own example: Group A = 100 + 200 + 300 on the R365 side,
        // matched against a single 600 Bank row.
        var bank = new[] { Bank(1, 10, 600.00m, groupingKey: "A") };
        var r365 = new[]
        {
            R365(1, 8, 100.00m, groupingKey: "A"),
            R365(2, 9, 200.00m, groupingKey: "A"),
            R365(3, 10, 300.00m, groupingKey: "A"),
        };

        var matched = GroupingMatcher.MatchAll(bank, r365, DefaultSettings(), new GroupIdGenerator());

        Assert.Equal(1, matched);
        Assert.True(bank[0].IsMatched);
        Assert.Equal(MatchStatus.MatchedCombination, bank[0].Status);
        Assert.All(r365, r => Assert.True(r.IsMatched));
        Assert.All(r365, r => Assert.Equal(MatchStatus.MatchedCombination, r.Status));

        // All four rows (1 bank + 3 R365) share one GroupId, so sorting by
        // Match ID in Excel clusters them together.
        var groupId = bank[0].GroupId;
        Assert.All(r365, r => Assert.Equal(groupId, r.GroupId));
    }

    [Fact]
    public void MatchAll_ManyToManyTiesOutExactly_AllMembersMatched()
    {
        // Grouping doesn't require exactly one row on either side — just that
        // the two sides' totals agree.
        var bank = new[]
        {
            Bank(1, 10, 400.00m, groupingKey: "42"),
            Bank(2, 10, 200.00m, groupingKey: "42"),
        };
        var r365 = new[]
        {
            R365(1, 9, 350.00m, groupingKey: "42"),
            R365(2, 9, 250.00m, groupingKey: "42"),
        };

        var matched = GroupingMatcher.MatchAll(bank, r365, DefaultSettings(), new GroupIdGenerator());

        Assert.Equal(1, matched);
        Assert.All(bank, b => Assert.True(b.IsMatched));
        Assert.All(r365, r => Assert.True(r.IsMatched));
    }

    [Fact]
    public void MatchAll_BothSidesPresentButTotalsDiffer_FlagsManualReviewAndStillLocksAndGroups()
    {
        var bank = new[] { Bank(1, 10, 949652.13m, groupingKey: "6") };
        var r365 = new[]
        {
            R365(1, 9, 900000.00m, groupingKey: "6"),
            R365(2, 9, 47882.58m, groupingKey: "6"),
        };

        var matched = GroupingMatcher.MatchAll(bank, r365, DefaultSettings(), new GroupIdGenerator());

        Assert.Equal(0, matched); // did not tie out, so not counted as a match
        Assert.True(bank[0].IsMatched); // still locked — exclusivity, see class remarks
        Assert.Equal(MatchStatus.ManualReview, bank[0].Status);
        Assert.Contains("differs by", bank[0].Comment);
        Assert.All(r365, r => Assert.True(r.IsMatched));
        Assert.All(r365, r => Assert.Equal(bank[0].GroupId, r.GroupId)); // still visually grouped
    }

    [Fact]
    public void MatchAll_OnlyOneSideHasTheGroupingValue_FlagsNoMatchButStillGroupsAndLocks()
    {
        var bank = Array.Empty<TransactionRecord>();
        var r365 = new[]
        {
            R365(1, 9, 500.00m, groupingKey: "99"),
            R365(2, 9, 300.00m, groupingKey: "99"),
        };

        var matched = GroupingMatcher.MatchAll(bank, r365, DefaultSettings(), new GroupIdGenerator());

        Assert.Equal(0, matched);
        Assert.All(r365, r => Assert.True(r.IsMatched)); // locked so it's never picked up by a later pass
        Assert.All(r365, r => Assert.Equal(MatchStatus.NoMatch, r.Status));
        Assert.Equal(r365[0].GroupId, r365[1].GroupId); // grouped together despite having no Bank counterpart
    }

    [Fact]
    public void MatchAll_BlankGroupingKey_LeavesRowsUntouchedForNormalMatchingLogic()
    {
        var bank = new[] { Bank(1, 10, 50.00m) }; // no groupingKey => blank
        var r365 = new[] { R365(1, 10, 50.00m, groupingKey: "") };

        GroupingMatcher.MatchAll(bank, r365, DefaultSettings(), new GroupIdGenerator());

        Assert.False(bank[0].IsMatched);
        Assert.False(r365[0].IsMatched);
    }

    [Fact]
    public void MatchAll_AlreadyMatchedRows_AreExcludedFromBucketing()
    {
        // Simulates the real ordering: a named rule (e.g. Sysco, run earlier
        // in ReconciliationEngine) already claimed these rows under the same
        // literal Grouping text before GroupingMatcher ever runs. This is the
        // mechanism that makes the Sysco/Grouping split correct with no
        // special-casing — see GroupingMatcher's class remarks.
        var bank = new[] { Bank(1, 10, 1000.00m, groupingKey: "Sysco") };
        var r365 = new[] { R365(1, 10, 1000.00m, groupingKey: "Sysco") };
        bank[0].IsMatched = true;
        bank[0].Status = MatchStatus.MatchedCombination;
        bank[0].GroupId = 7;
        r365[0].IsMatched = true;
        r365[0].Status = MatchStatus.MatchedCombination;
        r365[0].GroupId = 7;

        var matched = GroupingMatcher.MatchAll(bank, r365, DefaultSettings(), new GroupIdGenerator());

        Assert.Equal(0, matched); // nothing new bucketed — both rows were already locked
        Assert.Equal(7, bank[0].GroupId); // untouched, still the named rule's group
    }

    [Fact]
    public void MatchAll_GroupingKeyComparisonIsCaseInsensitive()
    {
        var bank = new[] { Bank(1, 10, 100.00m, groupingKey: "group10") };
        var r365 = new[] { R365(1, 10, 100.00m, groupingKey: "GROUP10") };

        var matched = GroupingMatcher.MatchAll(bank, r365, DefaultSettings(), new GroupIdGenerator());

        Assert.Equal(1, matched);
        Assert.True(bank[0].IsMatched);
        Assert.True(r365[0].IsMatched);
    }

    [Fact]
    public void MatchAll_DifferentGroupingValues_NeverBucketedTogether()
    {
        var bank = new[]
        {
            Bank(1, 10, 100.00m, groupingKey: "A"),
            Bank(2, 10, 100.00m, groupingKey: "B"),
        };
        var r365 = new[]
        {
            R365(1, 10, 100.00m, groupingKey: "A"),
            R365(2, 10, 100.00m, groupingKey: "B"),
        };

        var matched = GroupingMatcher.MatchAll(bank, r365, DefaultSettings(), new GroupIdGenerator());

        Assert.Equal(2, matched);
        Assert.NotEqual(bank[0].GroupId, bank[1].GroupId);
    }

    [Fact]
    public void MatchAll_RespectsConfiguredAmountTolerance()
    {
        var settings = DefaultSettings();
        settings.AmountToleranceDollars = 0.02m;

        var bank = new[] { Bank(1, 10, 100.00m, groupingKey: "A") };
        var r365 = new[] { R365(1, 10, 100.01m, groupingKey: "A") }; // 1 cent off, within tolerance

        var matched = GroupingMatcher.MatchAll(bank, r365, settings, new GroupIdGenerator());

        Assert.Equal(1, matched);
        Assert.Equal(MatchStatus.MatchedCombination, bank[0].Status);
    }
}
