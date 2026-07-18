using BankReconciliation.Core.Matching;
using BankReconciliation.Core.Models;
using Xunit;
using static BankReconciliation.Core.Tests.TestHelpers;

namespace BankReconciliation.Core.Tests;

public class CombinationMatcherTests
{
    [Fact]
    public void FindCombination_TwoItemExactSum_Found()
    {
        var candidates = new[]
        {
            R365(1, 5, -40.00m),
            R365(2, 5, -60.00m),
            R365(3, 5, -999.00m), // decoy, should not be picked
        };
        var stats = new CombinationMatcher.SearchStats();

        var result = CombinationMatcher.FindCombination(-10000, candidates, DefaultSettings(), stats, CancellationToken.None);

        Assert.Equal(CombinationMatcher.Outcome.Found, result.Outcome);
        Assert.Equal(2, result.Combination!.Count);
        Assert.Equal(-10000, result.Combination.Sum(c => c.AmountCents));
        Assert.Equal(1, stats.TwoSumHits);
    }

    [Fact]
    public void FindCombination_ThreeItemExactSum_Found()
    {
        var candidates = new[]
        {
            R365(1, 5, -10.00m),
            R365(2, 5, -25.00m),
            R365(3, 5, -65.00m),
            R365(4, 5, -12.34m), // decoy
        };
        var stats = new CombinationMatcher.SearchStats();

        var result = CombinationMatcher.FindCombination(-10000, candidates, DefaultSettings(), stats, CancellationToken.None);

        Assert.Equal(CombinationMatcher.Outcome.Found, result.Outcome);
        Assert.Equal(3, result.Combination!.Count);
        Assert.Equal(-10000, result.Combination.Sum(c => c.AmountCents));
        Assert.Equal(1, stats.ThreeSumHits);
    }

    [Fact]
    public void FindCombination_LargerGroup_FoundViaBoundedDp()
    {
        // 7 items that must ALL be used to hit the target — forces the search
        // past the 2-sum/3-sum fast paths into the DP stage.
        var candidates = new[]
        {
            R365(1, 5, -11.11m), R365(2, 5, -22.22m), R365(3, 5, -33.33m),
            R365(4, 5, -44.44m), R365(5, 5, -55.55m), R365(6, 5, -66.66m),
            R365(7, 5, -77.77m),
        };
        var target = candidates.Sum(c => c.AmountCents);
        var stats = new CombinationMatcher.SearchStats();

        var result = CombinationMatcher.FindCombination(target, candidates, DefaultSettings(), stats, CancellationToken.None);

        Assert.Equal(CombinationMatcher.Outcome.Found, result.Outcome);
        Assert.Equal(7, result.Combination!.Count);
        Assert.Equal(1, stats.DpHits);
    }

    [Fact]
    public void FindCombination_NoValidSubset_ReturnsNone()
    {
        var candidates = new[] { R365(1, 5, -10.00m), R365(2, 5, -20.00m), R365(3, 5, -33.00m) };
        var stats = new CombinationMatcher.SearchStats();

        var result = CombinationMatcher.FindCombination(-10000, candidates, DefaultSettings(), stats, CancellationToken.None);

        Assert.Equal(CombinationMatcher.Outcome.None, result.Outcome);
        Assert.Null(result.Combination);
    }

    [Fact]
    public void FindCombination_MixedSignPool_OnlySumsSameSignCandidates()
    {
        // A positive decoy sitting in an otherwise-negative pool must never be
        // pulled into a debit combination, even if it would make the math work.
        var candidates = new[]
        {
            R365(1, 5, -40.00m),
            R365(2, 5, -50.00m),
            R365(3, 5, 10.00m), // credit decoy: -40 + -50 + 10 would also hit a different target, but must be ignored for a -90 target search
        };
        var stats = new CombinationMatcher.SearchStats();

        var result = CombinationMatcher.FindCombination(-9000, candidates, DefaultSettings(), stats, CancellationToken.None);

        Assert.Equal(CombinationMatcher.Outcome.Found, result.Outcome);
        Assert.All(result.Combination!, c => Assert.True(c.AmountCents < 0));
    }

    [Fact]
    public void FindCombination_EveryReturnedMatch_SumsExactlyToTarget()
    {
        // Randomized-ish sanity sweep: whatever the search returns, the sum
        // invariant must hold. This is the property the whole engine leans on.
        var rnd = new Random(42);
        for (int trial = 0; trial < 25; trial++)
        {
            var count = rnd.Next(3, 15);
            var candidates = new List<TransactionRecord>();
            long total = 0;
            for (int i = 0; i < count; i++)
            {
                var cents = -rnd.Next(100, 99999);
                total += cents;
                candidates.Add(R365(i + 1, 5, cents / 100m));
            }
            // add a few decoys that don't belong to the target sum
            for (int i = 0; i < 5; i++)
                candidates.Add(R365(count + i + 1, 5, -rnd.Next(100, 99999) / 100m));

            var stats = new CombinationMatcher.SearchStats();
            var result = CombinationMatcher.FindCombination(total, candidates, DefaultSettings(), stats, CancellationToken.None);

            if (result.Outcome == CombinationMatcher.Outcome.Found)
                Assert.Equal(total, result.Combination!.Sum(c => c.AmountCents));
        }
    }

    [Fact]
    public void ProcessAll_OnlyGroupedR365RowsAreEligibleForCombinations_WhenRequireGroupKeywordIsOn()
    {
        // Two non-grouped rows that would sum exactly to the bank amount must
        // NOT be combined when RequireGroupKeywordForCombinations is
        // explicitly turned on (the original spec's Rule 1/Rule 2 split, now
        // opt-in rather than the default — see ReconciliationSettings).
        var bank = new[] { Bank(1, 10, -100.00m) };
        var r365 = new[]
        {
            R365(1, 8, -60.00m, grouped: false),
            R365(2, 8, -40.00m, grouped: false),
        };
        var settings = DefaultSettings();
        settings.RequireGroupKeywordForCombinations = true;
        var stats = new CombinationMatcher.SearchStats();

        CombinationMatcher.ProcessAll(bank, r365, settings, new GroupIdGenerator(), progress: null, CancellationToken.None, stats);

        Assert.False(bank[0].IsMatched);
    }

    [Fact]
    public void ProcessAll_NonGroupedRowsAreEligibleByDefault()
    {
        // Same setup as above, but with default settings (the new default:
        // RequireGroupKeywordForCombinations = false) — non-grouped rows
        // ARE now eligible for combination matching.
        var bank = new[] { Bank(1, 10, -100.00m) };
        var r365 = new[]
        {
            R365(1, 8, -60.00m, grouped: false),
            R365(2, 8, -40.00m, grouped: false),
        };
        var stats = new CombinationMatcher.SearchStats();

        CombinationMatcher.ProcessAll(bank, r365, DefaultSettings(), new GroupIdGenerator(), progress: null, CancellationToken.None, stats);

        Assert.True(bank[0].IsMatched);
    }

    // ---- RunSpecialComboRules (named/curated rules — a separate pipeline
    // stage from ProcessAll, called on its own, BEFORE Pass 1) -------------

    [Fact]
    public void RunSpecialComboRules_MatchesAcrossWideDateGapIgnoringNormalWindow()
    {
        // The default named rule (bank column 5 contains "Sysco" <-> R365
        // Grouping column 4 also contains "Sysco") must group rows even when
        // they are much further apart than MaxDateDifferenceDays allows,
        // since named rules are not subject to the normal date window at all.
        var bank = new[]
        {
            new TransactionRecord
            {
                RowNumber = 1,
                Side = TransactionSide.Bank,
                Date = new DateTime(2026, 7, 1),
                AmountCents = -18000,
                RawColumns = new Dictionary<int, string> { [5] = "ACH Sysco Foods Payment" },
            },
        };
        var r365 = new[]
        {
            // 30+ days before the bank date — would fail the normal window.
            new TransactionRecord { RowNumber = 1, Side = TransactionSide.R365, Date = new DateTime(2026, 6, 1), AmountCents = -10000, RawColumns = new Dictionary<int, string> { [4] = "Sysco" } },
            new TransactionRecord { RowNumber = 2, Side = TransactionSide.R365, Date = new DateTime(2026, 6, 6), AmountCents = -8000, RawColumns = new Dictionary<int, string> { [4] = "Sysco" } },
        };
        var settings = DefaultSettings();
        settings.MaxDateDifferenceDays = 6; // far narrower than the gap above
        var stats = new CombinationMatcher.SearchStats();

        CombinationMatcher.RunSpecialComboRules(bank, r365, settings, new GroupIdGenerator(), stats, CancellationToken.None);

        Assert.True(bank[0].IsMatched);
        Assert.Equal(MatchStatus.MatchedCombination, bank[0].Status);
        Assert.All(r365, r => Assert.True(r.IsMatched));
        Assert.Equal(bank[0].GroupId, r365[0].GroupId);
        Assert.Equal(bank[0].GroupId, r365[1].GroupId);
    }

    [Fact]
    public void RunSpecialComboRules_DoesNotFireWithoutBankKeyword()
    {
        // A bank row whose column 5 does NOT contain "Sysco" must not be
        // grouped with Sysco-tagged R365 rows even if the amounts would sum
        // correctly — the named rule is keyword-gated on both sides.
        var bank = new[]
        {
            new TransactionRecord
            {
                RowNumber = 1,
                Side = TransactionSide.Bank,
                Date = new DateTime(2026, 7, 1),
                AmountCents = -18000,
                RawColumns = new Dictionary<int, string> { [5] = "Wire Transfer - Vendor Payment" },
            },
        };
        var r365 = new[]
        {
            new TransactionRecord { RowNumber = 1, Side = TransactionSide.R365, Date = new DateTime(2026, 6, 1), AmountCents = -10000, RawColumns = new Dictionary<int, string> { [4] = "Sysco" } },
            new TransactionRecord { RowNumber = 2, Side = TransactionSide.R365, Date = new DateTime(2026, 6, 6), AmountCents = -8000, RawColumns = new Dictionary<int, string> { [4] = "Sysco" } },
        };
        var settings = DefaultSettings();
        settings.MaxDateDifferenceDays = 6;
        var stats = new CombinationMatcher.SearchStats();

        CombinationMatcher.RunSpecialComboRules(bank, r365, settings, new GroupIdGenerator(), stats, CancellationToken.None);

        Assert.False(bank[0].IsMatched);
    }

    [Fact]
    public void RunSpecialComboRules_MatchesEvenWhenAmountsDontSumAtAll()
    {
        // The rule performs no arithmetic whatsoever: only $110.00 of
        // Sysco-tagged R365 rows exist against a $180.00 Sysco charge, and
        // nothing sums to anything. It must still match everything it can
        // find rather than leaving the row as No Match — any gap is left
        // visible via the AB formula, not by refusing to match.
        var bank = new[]
        {
            new TransactionRecord
            {
                RowNumber = 1,
                Side = TransactionSide.Bank,
                Date = new DateTime(2026, 7, 1),
                AmountCents = -18000,
                RawColumns = new Dictionary<int, string> { [5] = "ACH Sysco Foods Payment" },
            },
        };
        var r365 = new[]
        {
            new TransactionRecord { RowNumber = 1, Side = TransactionSide.R365, Date = new DateTime(2026, 6, 1), AmountCents = -5000, RawColumns = new Dictionary<int, string> { [4] = "Sysco" } },
            new TransactionRecord { RowNumber = 2, Side = TransactionSide.R365, Date = new DateTime(2026, 6, 6), AmountCents = -6000, RawColumns = new Dictionary<int, string> { [4] = "Sysco" } },
        };
        var settings = DefaultSettings();
        var stats = new CombinationMatcher.SearchStats();

        CombinationMatcher.RunSpecialComboRules(bank, r365, settings, new GroupIdGenerator(), stats, CancellationToken.None);

        Assert.True(bank[0].IsMatched);
        Assert.Equal(MatchStatus.MatchedCombination, bank[0].Status);
        Assert.All(r365, r => Assert.True(r.IsMatched));
        Assert.Equal(bank[0].GroupId, r365[0].GroupId);
        Assert.Equal(bank[0].GroupId, r365[1].GroupId);
    }

    [Fact]
    public void RunSpecialComboRules_GroupsEveryEligibleRowTogetherRegardlessOfSignOrDate()
    {
        // The rule is a pure keyword filter, not a search: EVERY still-
        // unmatched Sysco bank row and EVERY still-unmatched Sysco-tagged
        // R365 row are grouped into one single match together, no matter how
        // mismatched their amounts, signs, or dates are relative to each
        // other. Two unrelated Sysco entries (opposite signs, unrelated
        // magnitudes, six months apart) end up sharing one Match ID with two
        // equally unrelated Sysco-tagged entries.
        var bank = new[]
        {
            new TransactionRecord
            {
                RowNumber = 1,
                Side = TransactionSide.Bank,
                Date = new DateTime(2026, 1, 1),
                AmountCents = -500000, // -$5,000.00
                RawColumns = new Dictionary<int, string> { [5] = "ACH Sysco Foods Payment" },
            },
            new TransactionRecord
            {
                RowNumber = 2,
                Side = TransactionSide.Bank,
                Date = new DateTime(2026, 6, 15),
                AmountCents = 250, // +$2.50 — opposite sign, unrelated magnitude
                RawColumns = new Dictionary<int, string> { [5] = "Sysco Corp Refund" },
            },
        };
        var r365 = new[]
        {
            new TransactionRecord { RowNumber = 1, Side = TransactionSide.R365, Date = new DateTime(2026, 6, 1), AmountCents = -5000, RawColumns = new Dictionary<int, string> { [4] = "Sysco" } },
            // opposite sign, unrelated amount, far-off date
            new TransactionRecord { RowNumber = 2, Side = TransactionSide.R365, Date = new DateTime(2026, 12, 18), AmountCents = 99999, RawColumns = new Dictionary<int, string> { [4] = "Sysco" } },
        };
        var settings = DefaultSettings();
        var stats = new CombinationMatcher.SearchStats();

        CombinationMatcher.RunSpecialComboRules(bank, r365, settings, new GroupIdGenerator(), stats, CancellationToken.None);

        Assert.All(bank, b => Assert.True(b.IsMatched));
        Assert.All(bank, b => Assert.Equal(MatchStatus.MatchedCombination, b.Status));
        Assert.All(r365, r => Assert.True(r.IsMatched));
        var groupId = bank[0].GroupId;
        Assert.Equal(groupId, bank[1].GroupId);
        Assert.Equal(groupId, r365[0].GroupId);
        Assert.Equal(groupId, r365[1].GroupId);
    }

    [Fact]
    public void RunSpecialComboRules_DoesNotFireWhenOnlyOneSideHasEligibleRows()
    {
        // A Sysco bank row with zero Sysco-tagged R365 rows anywhere in the
        // file must be left alone (falls through to later passes / No Match)
        // rather than the rule inventing a match with nothing on the other
        // side.
        var bank = new[]
        {
            new TransactionRecord
            {
                RowNumber = 1,
                Side = TransactionSide.Bank,
                Date = new DateTime(2026, 7, 1),
                AmountCents = -18000,
                RawColumns = new Dictionary<int, string> { [5] = "ACH Sysco Foods Payment" },
            },
        };
        var r365 = new[]
        {
            new TransactionRecord { RowNumber = 1, Side = TransactionSide.R365, Date = new DateTime(2026, 6, 1), AmountCents = -18000, RawColumns = new Dictionary<int, string> { [4] = "Check #4471" } },
        };
        var settings = DefaultSettings();
        var stats = new CombinationMatcher.SearchStats();

        CombinationMatcher.RunSpecialComboRules(bank, r365, settings, new GroupIdGenerator(), stats, CancellationToken.None);

        Assert.False(bank[0].IsMatched);
        Assert.False(r365[0].IsMatched);
    }

    [Fact]
    public void RunSpecialComboRules_UsesEachRulesOwnColumn_NotTheDefaultDescriptionColumn()
    {
        // A custom rule pointed at a DIFFERENT column than the shipped
        // default (14 on the bank side, 20 on the R365 side) must be checked
        // against THAT column, not column 8/16 — proving column choice is
        // genuinely per-rule rather than hard-coded.
        var bank = new[]
        {
            new TransactionRecord
            {
                RowNumber = 1,
                Side = TransactionSide.Bank,
                Date = new DateTime(2026, 7, 1),
                AmountCents = -5000,
                RawColumns = new Dictionary<int, string> { [8] = "nothing relevant here", [14] = "Voided Check" },
            },
        };
        var r365 = new[]
        {
            new TransactionRecord { RowNumber = 1, Side = TransactionSide.R365, Date = new DateTime(2026, 6, 1), AmountCents = -1200, RawColumns = new Dictionary<int, string> { [16] = "nothing relevant here", [20] = "Voided" } },
        };
        var settings = DefaultSettings();
        settings.SpecialComboRules = new List<SpecialComboRule>
        {
            new() { BankColumn = 14, BankKeyword = "Voided", R365Column = 20, R365Keyword = "Voided" },
        };
        var stats = new CombinationMatcher.SearchStats();

        CombinationMatcher.RunSpecialComboRules(bank, r365, settings, new GroupIdGenerator(), stats, CancellationToken.None);

        Assert.True(bank[0].IsMatched);
        Assert.True(r365[0].IsMatched);
        Assert.Equal(bank[0].GroupId, r365[0].GroupId);
    }

    [Fact]
    public void ProcessAll_MatchedGroupMembers_AllShareSameGroupIdAndComment()
    {
        var bank = new[] { Bank(1, 10, -300.00m) };
        var r365 = new[]
        {
            R365(1, 8, -120.00m),
            R365(2, 9, -80.00m),
            R365(3, 6, -100.00m),
        };
        var stats = new CombinationMatcher.SearchStats();

        CombinationMatcher.ProcessAll(bank, r365, DefaultSettings(), new GroupIdGenerator(), progress: null, CancellationToken.None, stats);

        Assert.True(bank[0].IsMatched);
        Assert.Equal(MatchStatus.MatchedCombination, bank[0].Status);
        Assert.Equal("Matched (Combination of 3 Transactions)", bank[0].Comment);
        Assert.All(r365, r => Assert.True(r.IsMatched));
        Assert.All(r365, r => Assert.Equal(bank[0].GroupId, r.GroupId));
        Assert.Equal(1, stats.MatchedCount);
    }

    [Fact]
    public void ProcessAll_LockedR365Row_NeverReusedByASecondBankTransaction()
    {
        // Only one R365 candidate of $50 exists; two bank transactions each
        // need a $50 component. At most one of them may complete.
        var bank = new[] { Bank(1, 10, -50.00m), Bank(2, 10, -50.00m) };
        var r365 = new[] { R365(1, 8, -50.00m) };
        var stats = new CombinationMatcher.SearchStats();

        CombinationMatcher.ProcessAll(bank, r365, DefaultSettings(), new GroupIdGenerator(), progress: null, CancellationToken.None, stats);

        var matchedCount = bank.Count(b => b.IsMatched);
        Assert.True(matchedCount <= 1);
    }

    [Fact]
    public void FindCombination_PoolLargerThanCap_IsTruncatedNotSilentlyIgnored()
    {
        var settings = DefaultSettings();
        settings.MaxCombinationPoolSize = 5;

        var candidates = Enumerable.Range(1, 20)
            .Select(i => R365(i, 5, -i)) // 20 distinct-amount candidates, cap is 5
            .ToList();
        var stats = new CombinationMatcher.SearchStats();

        // Target unreachable by the 5 closest-to-date candidates alone (they're
        // all the SAME date here, so "closest" is arbitrary — the point is the
        // pool size cap is respected and reported).
        var target = candidates.Sum(c => c.AmountCents); // needs all 20
        var result = CombinationMatcher.FindCombination(target, candidates, settings, stats, CancellationToken.None);

        Assert.True(stats.TruncatedPools >= 1);
        Assert.NotEqual(CombinationMatcher.Outcome.Found, result.Outcome);
    }
}
