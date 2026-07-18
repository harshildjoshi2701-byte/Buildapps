namespace BankReconciliation.Core.Matching;

/// <summary>
/// Turns match characteristics into a 0-100 confidence score. Pulled out as
/// its own class (rather than inlined in the matchers) so the scoring
/// formula is a single, testable, tunable place — see README "Confidence
/// Score Formula" for the rationale behind the specific weights.
/// </summary>
public static class ConfidenceScorer
{
    /// <summary>Exact-date, one-to-one match. Always 100 — nothing eroded it.</summary>
    public const int ExactMatchScore = 100;

    /// <summary>One-to-one match found within the allowed date window but not on
    /// the exact date. Loses up to 10 points, scaled linearly by how much of
    /// the allowed window was used, floored at 90 (matches the spec's
    /// "100%, 98%, 95%, 90%" examples).</summary>
    public static int OneToOneDateTolerant(int dateDiffDays, int maxAllowedDays)
    {
        if (dateDiffDays <= 0) return ExactMatchScore;
        if (maxAllowedDays <= 0) return ExactMatchScore;
        var penalty = dateDiffDays / (double)maxAllowedDays * 10.0;
        return Math.Max(90, (int)Math.Round(ExactMatchScore - penalty));
    }

    /// <summary>
    /// Combination match score. Three independent penalties are subtracted
    /// from a perfect 100:
    ///   - Date penalty: up to 5 points, scaled by how close the group's
    ///     WORST (largest) member date-difference is to the allowed maximum.
    ///   - Count penalty: up to 8 points, logarithmic in transaction count —
    ///     a 40-transaction combination is intrinsically a little less
    ///     certain than a 2-transaction one, but the penalty must not keep
    ///     growing unbounded for the legitimate 100+ transaction case the
    ///     spec explicitly calls out, hence the log2 shape and the cap.
    ///   - Ambiguity penalty: 15 points if the search found more than one
    ///     equally-valid (same size, different members) combination summing
    ///     to the target. Combinations that hit this penalty are always
    ///     routed to Manual Review by the caller regardless of the resulting
    ///     score — the penalty mainly exists so the score itself communicates
    ///     *why* in logs and the UI.
    /// </summary>
    public static int Combination(int transactionCount, int maxDateDiffDays, int maxAllowedDays, bool ambiguous)
    {
        double score = ExactMatchScore;

        if (maxAllowedDays > 0)
            score -= maxDateDiffDays / (double)maxAllowedDays * 5.0;

        var countPenalty = Math.Log2(Math.Max(transactionCount, 1)) * 1.5;
        score -= Math.Min(8.0, countPenalty);

        if (ambiguous)
            score -= 15.0;

        return Math.Clamp((int)Math.Round(score), 0, 100);
    }
}
