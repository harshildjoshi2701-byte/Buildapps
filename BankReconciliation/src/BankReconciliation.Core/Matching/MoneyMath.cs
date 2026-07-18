namespace BankReconciliation.Core.Matching;

/// <summary>
/// Every amount comparison in the engine happens in integer cents, never in
/// <c>decimal</c> or <c>double</c>. Two dollar amounts that are "the same"
/// after rounding can differ in their 3rd+ decimal place depending on how
/// they were computed upstream (e.g. a credit-minus-debit derivation), and
/// naive decimal equality would then incorrectly reject a genuine exact
/// match. Converting once to cents at load time and comparing longs
/// everywhere after that removes the entire category of bug.
/// </summary>
public static class MoneyMath
{
    public static long ToCents(decimal dollars) => (long)Math.Round(dollars * 100m, MidpointRounding.AwayFromZero);

    public static decimal ToDollars(long cents) => cents / 100m;

    /// <summary>True if two signed amounts match within the configured tolerance.
    /// Tolerance is specified in dollars for readability in Settings and
    /// converted to cents here.</summary>
    public static bool AmountsEqual(long a, long b, decimal toleranceDollars)
    {
        var toleranceCents = ToCents(toleranceDollars);
        return Math.Abs(a - b) <= toleranceCents;
    }

    /// <summary>Same sign (both credits or both debits). Zero is neither — a
    /// zero-amount row can never participate in matching.</summary>
    public static bool SameSign(long a, long b) => a != 0 && b != 0 && (a > 0) == (b > 0);
}
