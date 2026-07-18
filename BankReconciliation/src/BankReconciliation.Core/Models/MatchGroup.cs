namespace BankReconciliation.Core.Models;

/// <summary>
/// One resolved (or attempted) match: a single bank transaction and the one-or-more
/// R365 transactions it was matched against. For one-to-one matches
/// <see cref="R365Transactions"/> has exactly one element.
/// </summary>
public sealed class MatchGroup
{
    public required int GroupId { get; init; }
    public required MatchStatus Status { get; init; }
    public required TransactionRecord BankTransaction { get; init; }
    public required IReadOnlyList<TransactionRecord> R365Transactions { get; init; }
    public required int ConfidenceScore { get; init; }
    public required string Comment { get; init; }

    public int TransactionCount => 1 + R365Transactions.Count;
    public long TotalAmountCents => BankTransaction.AmountCents;
    public int MaxDateDifferenceDays => R365Transactions.Count == 0
        ? 0
        : R365Transactions.Max(r => (BankTransaction.Date - r.Date).Days);
}
