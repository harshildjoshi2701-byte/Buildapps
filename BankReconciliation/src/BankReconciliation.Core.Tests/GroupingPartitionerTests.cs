using BankReconciliation.Core.Matching;
using BankReconciliation.Core.Models;
using Xunit;
using static BankReconciliation.Core.Tests.TestHelpers;

namespace BankReconciliation.Core.Tests;

/// <summary>Unit tests for the partitioning/residual-flagging MECHANISM in
/// isolation. The actual matching behavior a Grouping partition produces
/// once real matchers run against it (ties out via combination search,
/// doesn't tie out, one-sided, a large vendor-tag-style group) is an
/// end-to-end concern of the full pipeline — see
/// ReconciliationEngineTests for those scenarios.</summary>
public class GroupingPartitionerTests
{
    [Fact]
    public void BuildPartitions_SameGroupingKey_EndUpInOnePartitionTogether()
    {
        var bank = new[] { Bank(1, 10, 600.00m, groupingKey: "A") };
        var r365 = new[]
        {
            R365(1, 8, 100.00m, groupingKey: "A"),
            R365(2, 9, 200.00m, groupingKey: "A"),
            R365(3, 10, 300.00m, groupingKey: "A"),
        };

        var partitions = GroupingPartitioner.BuildPartitions(bank, r365);

        var partitionA = Assert.Single(partitions, p => p.Key == "A");
        Assert.True(partitionA.IsGrouped);
        Assert.Single(partitionA.Bank);
        Assert.Equal(3, partitionA.R365.Count);
    }

    [Fact]
    public void BuildPartitions_DifferentGroupingValues_NeverShareAPartition()
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

        var partitions = GroupingPartitioner.BuildPartitions(bank, r365);

        var partitionA = partitions.Single(p => p.Key == "A");
        var partitionB = partitions.Single(p => p.Key == "B");
        Assert.DoesNotContain(bank[1], partitionA.Bank);
        Assert.DoesNotContain(r365[1], partitionA.R365);
        Assert.DoesNotContain(bank[0], partitionB.Bank);
        Assert.DoesNotContain(r365[0], partitionB.R365);
    }

    [Fact]
    public void BuildPartitions_BlankGroupingKey_AllLandInOneUngroupedPartition()
    {
        var bank = new[] { Bank(1, 10, 50.00m), Bank(2, 11, 75.00m) }; // no groupingKey => blank
        var r365 = new[] { R365(1, 10, 50.00m, groupingKey: "") };

        var partitions = GroupingPartitioner.BuildPartitions(bank, r365);

        var ungrouped = Assert.Single(partitions, p => !p.IsGrouped);
        Assert.Equal(GroupingPartitioner.UngroupedKey, ungrouped.Key);
        Assert.Equal(2, ungrouped.Bank.Count);
        Assert.Single(ungrouped.R365);
        Assert.DoesNotContain(partitions, p => p.IsGrouped && p.Key == GroupingPartitioner.UngroupedKey);
    }

    [Fact]
    public void BuildPartitions_UngroupedPartitionIsAlwaysPresentEvenWhenEmpty()
    {
        var bank = new[] { Bank(1, 10, 100.00m, groupingKey: "A") };
        var r365 = new[] { R365(1, 10, 100.00m, groupingKey: "A") };

        var partitions = GroupingPartitioner.BuildPartitions(bank, r365);

        Assert.Contains(partitions, p => !p.IsGrouped);
    }

    [Fact]
    public void BuildPartitions_GroupingKeyComparisonIsCaseInsensitive()
    {
        var bank = new[] { Bank(1, 10, 100.00m, groupingKey: "group10") };
        var r365 = new[] { R365(1, 10, 100.00m, groupingKey: "GROUP10") };

        var partitions = GroupingPartitioner.BuildPartitions(bank, r365);

        var grouped = Assert.Single(partitions, p => p.IsGrouped);
        Assert.Single(grouped.Bank);
        Assert.Single(grouped.R365);
    }

    [Fact]
    public void BuildPartitions_AlreadyMatchedRows_AreExcludedEntirely()
    {
        // Simulates a named rule (RunSpecialComboRules) already claiming a
        // row under some Grouping text before partitioning ever runs.
        var bank = new[] { Bank(1, 10, 1000.00m, groupingKey: "Sysco") };
        var r365 = new[] { R365(1, 10, 1000.00m, groupingKey: "Sysco") };
        bank[0].IsMatched = true;
        bank[0].Status = MatchStatus.MatchedCombination;

        var partitions = GroupingPartitioner.BuildPartitions(bank, r365);

        // Bank row excluded (already matched); R365 row still gets its own
        // one-sided "Sysco" partition since it wasn't matched by the rule.
        var syscoPartition = Assert.Single(partitions, p => p.Key == "Sysco");
        Assert.Empty(syscoPartition.Bank);
        Assert.Single(syscoPartition.R365);
    }

    [Fact]
    public void BuildPartitions_OneSidedGroupingValue_StillProducesAPartition()
    {
        var bank = Array.Empty<TransactionRecord>();
        var r365 = new[]
        {
            R365(1, 9, 500.00m, groupingKey: "99"),
            R365(2, 9, 300.00m, groupingKey: "99"),
        };

        var partitions = GroupingPartitioner.BuildPartitions(bank, r365);

        var partition99 = Assert.Single(partitions, p => p.Key == "99");
        Assert.Empty(partition99.Bank);
        Assert.Equal(2, partition99.R365.Count);
    }

    [Fact]
    public void FlagUnresolvedResidual_BothSidesStillHaveLeftovers_FlagsAllAsManualReviewWithNoRealGroupId()
    {
        var bank = new[] { Bank(1, 10, 949652.13m, groupingKey: "6") };
        var r365 = new[]
        {
            R365(1, 9, 900000.00m, groupingKey: "6"),
            R365(2, 9, 47882.58m, groupingKey: "6"),
        };
        var partition = new GroupingPartitioner.Partition { Key = "6", IsGrouped = true, Bank = bank.ToList(), R365 = r365.ToList() };

        GroupingPartitioner.FlagUnresolvedResidual(partition);

        Assert.Equal(MatchStatus.ManualReview, bank[0].Status);
        Assert.Contains("residual", bank[0].Comment);
        Assert.All(r365, r => Assert.Equal(MatchStatus.ManualReview, r.Status));
        // Not a genuine match: no real GroupId, and IsMatched left false so a
        // later stage's own !IsMatched filtering still treats it correctly.
        Assert.Equal(-1, bank[0].GroupId);
        Assert.All(r365, r => Assert.Equal(-1, r.GroupId));
        Assert.False(bank[0].IsMatched);
    }

    [Fact]
    public void FlagUnresolvedResidual_ManyLeftoversOnBothSides_DoesNotBundleThemIntoOneManualReviewBlob()
    {
        // Regression test for a real finding: a large, lopsided vendor-tag
        // partition (e.g. real "Sysco" data: 2,541 Bank rows vs 100 R365
        // rows, most on each side genuinely unrelated to anything on the
        // other) left thousands of rows unmatched on BOTH sides after real
        // matching. The original (unbounded) version of this method flagged
        // every one of them as Manual Review sharing ONE comment — the exact
        // same "thousands of unrelated rows given one verdict" failure this
        // whole redesign exists to fix, just wearing a different status
        // label. Past MaxResidualRowsToFlag, leftovers must be left alone.
        var bank = Enumerable.Range(0, 15).Select(i => Bank(i + 1, 0, 100.00m + i, groupingKey: "VendorX")).ToList();
        var r365 = Enumerable.Range(0, 10).Select(i => R365(i + 1, 0, 900.00m + i, groupingKey: "VendorX")).ToList();
        var partition = new GroupingPartitioner.Partition { Key = "VendorX", IsGrouped = true, Bank = bank, R365 = r365 };

        GroupingPartitioner.FlagUnresolvedResidual(partition);

        Assert.All(bank, b => Assert.Equal(MatchStatus.Unmatched, b.Status));
        Assert.All(r365, r => Assert.Equal(MatchStatus.Unmatched, r.Status));
    }

    [Fact]
    public void FlagUnresolvedResidual_ExactlyAtTheResidualBound_StillFlags()
    {
        // 12 + 8 = 20, the documented boundary — must still be flagged (only
        // strictly MORE than the bound is left alone).
        var bank = Enumerable.Range(0, 12).Select(i => Bank(i + 1, 0, 100.00m + i, groupingKey: "G")).ToList();
        var r365 = Enumerable.Range(0, 8).Select(i => R365(i + 1, 0, 900.00m + i, groupingKey: "G")).ToList();
        var partition = new GroupingPartitioner.Partition { Key = "G", IsGrouped = true, Bank = bank, R365 = r365 };

        GroupingPartitioner.FlagUnresolvedResidual(partition);

        Assert.All(bank, b => Assert.Equal(MatchStatus.ManualReview, b.Status));
        Assert.All(r365, r => Assert.Equal(MatchStatus.ManualReview, r.Status));
    }

    [Fact]
    public void FlagUnresolvedResidual_OnlyOneSideHasLeftovers_LeavesRowsUnmatchedForNormalFinalization()
    {
        var r365 = new[]
        {
            R365(1, 9, 500.00m, groupingKey: "99"),
            R365(2, 9, 300.00m, groupingKey: "99"),
        };
        var partition = new GroupingPartitioner.Partition { Key = "99", IsGrouped = true, Bank = new List<TransactionRecord>(), R365 = r365.ToList() };

        GroupingPartitioner.FlagUnresolvedResidual(partition);

        // Nothing to flag against on the other side — left as Unmatched for
        // DuplicateDetector.FinalizeUnmatched to turn into NoMatch/PossibleDuplicate.
        Assert.All(r365, r => Assert.Equal(MatchStatus.Unmatched, r.Status));
    }

    [Fact]
    public void FlagUnresolvedResidual_UngroupedPartition_NeverFlagsAnything()
    {
        var bank = new[] { Bank(1, 10, 100.00m) };
        var r365 = new[] { R365(1, 10, 999.00m) }; // deliberately non-matching, still ungrouped
        var partition = new GroupingPartitioner.Partition { Key = GroupingPartitioner.UngroupedKey, IsGrouped = false, Bank = bank.ToList(), R365 = r365.ToList() };

        GroupingPartitioner.FlagUnresolvedResidual(partition);

        Assert.Equal(MatchStatus.Unmatched, bank[0].Status);
        Assert.Equal(MatchStatus.Unmatched, r365[0].Status);
    }

    [Fact]
    public void FlagUnresolvedResidual_AlreadyMatchedMembers_AreNotReflaggedOrDuplicated()
    {
        var bank = new[] { Bank(1, 10, 100.00m, groupingKey: "A") };
        var r365 = new[]
        {
            R365(1, 10, 100.00m, groupingKey: "A"),
            R365(2, 11, 50.00m, groupingKey: "A"),
        };
        bank[0].IsMatched = true;
        bank[0].Status = MatchStatus.MatchedExact;
        r365[0].IsMatched = true;
        r365[0].Status = MatchStatus.MatchedExact;
        var partition = new GroupingPartitioner.Partition { Key = "A", IsGrouped = true, Bank = bank.ToList(), R365 = r365.ToList() };

        GroupingPartitioner.FlagUnresolvedResidual(partition);

        // The already-matched pair is untouched...
        Assert.Equal(MatchStatus.MatchedExact, bank[0].Status);
        Assert.Equal(MatchStatus.MatchedExact, r365[0].Status);
        // ...and the one genuine leftover R365 row has no Bank-side leftover
        // to be flagged against, so it stays Unmatched, not ManualReview.
        Assert.Equal(MatchStatus.Unmatched, r365[1].Status);
    }
}
