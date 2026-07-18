using BankReconciliation.Core.Matching;
using BankReconciliation.Core.Models;
using Xunit;
using static BankReconciliation.Core.Tests.TestHelpers;

namespace BankReconciliation.Core.Tests;

public class DuplicateDetectorTests
{
    [Fact]
    public void FinalizeUnmatched_TwoIdenticalUnmatchedRows_BothFlaggedPossibleDuplicate()
    {
        var a = Bank(1, 10, -200.00m);
        var b = Bank(2, 10, -200.00m);

        DuplicateDetector.FinalizeUnmatched(new[] { a, b });

        Assert.Equal(MatchStatus.PossibleDuplicate, a.Status);
        Assert.Equal(MatchStatus.PossibleDuplicate, b.Status);
    }

    [Fact]
    public void FinalizeUnmatched_UniqueUnmatchedRow_FlaggedNoMatch()
    {
        var a = Bank(1, 10, -200.00m);

        DuplicateDetector.FinalizeUnmatched(new[] { a });

        Assert.Equal(MatchStatus.NoMatch, a.Status);
    }

    [Fact]
    public void FinalizeUnmatched_AlreadyMatchedRows_AreNeverTouched()
    {
        var matched = Bank(1, 10, -200.00m);
        matched.IsMatched = true;
        matched.Status = MatchStatus.MatchedExact;
        matched.Comment = "Matched (Exact)";

        var duplicateOfMatched = Bank(2, 10, -200.00m); // same date/amount, but stays unmatched

        DuplicateDetector.FinalizeUnmatched(new[] { matched, duplicateOfMatched });

        Assert.Equal(MatchStatus.MatchedExact, matched.Status); // untouched
        Assert.Equal(MatchStatus.NoMatch, duplicateOfMatched.Status); // no OTHER unmatched row to pair with
    }

    [Fact]
    public void FinalizeUnmatched_SameAmountDifferentDate_NotFlaggedAsDuplicate()
    {
        var a = Bank(1, 10, -200.00m);
        var b = Bank(2, 11, -200.00m);

        DuplicateDetector.FinalizeUnmatched(new[] { a, b });

        Assert.Equal(MatchStatus.NoMatch, a.Status);
        Assert.Equal(MatchStatus.NoMatch, b.Status);
    }
}
