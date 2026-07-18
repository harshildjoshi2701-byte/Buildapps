using BankReconciliation.Core.Matching;
using Xunit;

namespace BankReconciliation.Core.Tests;

public class ConfidenceScorerTests
{
    [Fact]
    public void ExactDateOneToOne_Is100()
    {
        Assert.Equal(100, ConfidenceScorer.OneToOneDateTolerant(0, 6));
    }

    [Theory]
    [InlineData(1, 6)]
    [InlineData(3, 6)]
    [InlineData(6, 6)]
    public void DateTolerantOneToOne_NeverBelow90(int diff, int maxDays)
    {
        var score = ConfidenceScorer.OneToOneDateTolerant(diff, maxDays);
        Assert.InRange(score, 90, 99);
    }

    [Fact]
    public void DateTolerantOneToOne_MonotonicallyDecreasesWithDateDifference()
    {
        var s1 = ConfidenceScorer.OneToOneDateTolerant(1, 6);
        var s3 = ConfidenceScorer.OneToOneDateTolerant(3, 6);
        var s6 = ConfidenceScorer.OneToOneDateTolerant(6, 6);
        Assert.True(s1 >= s3);
        Assert.True(s3 >= s6);
    }

    [Fact]
    public void Combination_TwoItemsExactDate_ScoresHigherThanTwentyItemsWithDateSpread()
    {
        var tight = ConfidenceScorer.Combination(transactionCount: 2, maxDateDiffDays: 0, maxAllowedDays: 6, ambiguous: false);
        var loose = ConfidenceScorer.Combination(transactionCount: 20, maxDateDiffDays: 6, maxAllowedDays: 6, ambiguous: false);
        Assert.True(tight > loose);
        Assert.Equal(100, tight);
    }

    [Fact]
    public void Combination_AmbiguousMatch_ScoresLower()
    {
        var clean = ConfidenceScorer.Combination(3, 1, 6, ambiguous: false);
        var ambiguous = ConfidenceScorer.Combination(3, 1, 6, ambiguous: true);
        Assert.True(ambiguous < clean);
    }

    [Fact]
    public void Combination_LargeGroupSizeDoesNotCollapseScoreToZero()
    {
        // Spec explicitly allows 100+ transaction combinations; the count
        // penalty must stay bounded so a legitimately large, clean match
        // doesn't get needlessly punished into Manual Review territory.
        var score = ConfidenceScorer.Combination(transactionCount: 120, maxDateDiffDays: 0, maxAllowedDays: 6, ambiguous: false);
        Assert.True(score >= 90);
    }

    [Fact]
    public void Combination_ScoreIsAlwaysClampedToValidRange()
    {
        var score = ConfidenceScorer.Combination(transactionCount: 500, maxDateDiffDays: 6, maxAllowedDays: 6, ambiguous: true);
        Assert.InRange(score, 0, 100);
    }
}
