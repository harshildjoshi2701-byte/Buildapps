using BankReconciliation.Core.Models;

namespace BankReconciliation.Core.Tests;

/// <summary>Small factory helpers so individual tests can build transactions
/// in one line instead of repeating the required-property boilerplate.</summary>
internal static class TestHelpers
{
    private static readonly DateTime BaseDate = new(2026, 6, 1);

    public static TransactionRecord Bank(int row, int dayOffset, decimal amountDollars, string? reference = null) => new()
    {
        RowNumber = row,
        Side = TransactionSide.Bank,
        Date = BaseDate.AddDays(dayOffset),
        AmountCents = (long)Math.Round(amountDollars * 100m),
        Reference = reference ?? string.Empty,
    };

    public static TransactionRecord R365(int row, int dayOffset, decimal amountDollars, bool grouped = true, string? reference = null) => new()
    {
        RowNumber = row,
        Side = TransactionSide.R365,
        Date = BaseDate.AddDays(dayOffset),
        AmountCents = (long)Math.Round(amountDollars * 100m),
        Reference = reference ?? (grouped ? "R365-1000001" : "Online"),
        IsGroupedPosting = grouped,
    };

    public static ReconciliationSettings DefaultSettings() => new()
    {
        MaxDateDifferenceDays = 6,
        AmountToleranceDollars = 0.00m,
        MinCombinationConfidence = 80,
        MaxCombinationPoolSize = 300,
        MaxDpStates = 200_000,
        PerTransactionTimeBudgetSeconds = 2.0,
        GlobalCombinationTimeBudgetSeconds = 30.0,
        MaxThreads = 1, // deterministic, single-threaded by default in tests
    };
}
