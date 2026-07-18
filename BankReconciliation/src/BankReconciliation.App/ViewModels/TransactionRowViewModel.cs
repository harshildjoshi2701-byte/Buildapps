using BankReconciliation.Core.Models;

namespace BankReconciliation.App.ViewModels;

/// <summary>
/// Read-only display wrapper around a <see cref="TransactionRecord"/> for the
/// results grid. Deliberately NOT a live-binding wrapper (the underlying
/// record doesn't change after a run completes) — these are built once when
/// results arrive, which keeps the grid simple and fast even at thousands of
/// rows.
/// </summary>
public sealed class TransactionRowViewModel
{
    public TransactionRowViewModel(TransactionRecord record)
    {
        RowNumber = record.RowNumber;
        Side = record.Side == TransactionSide.Bank ? "Bank" : "R365";
        Date = record.Date;
        Amount = record.AmountDollars;
        Description = record.DescriptionSnippet;
        Status = record.Status;
        StatusText = FormatStatus(record.Status);
        Comment = record.Comment;
        Confidence = record.ConfidenceScore;
        GroupId = record.GroupId;
        IsGroupedPosting = record.IsGroupedPosting;
        GroupingKey = record.GroupingKey;
    }

    public int RowNumber { get; }
    public string Side { get; }
    public DateTime Date { get; }
    public decimal Amount { get; }
    public string Description { get; }
    public MatchStatus Status { get; }
    public string StatusText { get; }
    public string Comment { get; }
    public int Confidence { get; }
    public int GroupId { get; }
    public bool IsGroupedPosting { get; }
    public string GroupingKey { get; }

    public string AmountDisplay => Amount.ToString("C2");
    public string ConfidenceDisplay => Confidence > 0 ? $"{Confidence}%" : string.Empty;
    public string DateDisplay => Date.ToString("MM/dd/yyyy");

    private static string FormatStatus(MatchStatus status) => status switch
    {
        MatchStatus.MatchedExact => "Matched (Exact)",
        MatchStatus.MatchedDateTolerant => "Matched (Date Tolerant)",
        MatchStatus.MatchedCombination => "Matched (Combination)",
        MatchStatus.ManualReview => "Manual Review",
        MatchStatus.PossibleDuplicate => "Possible Duplicate",
        MatchStatus.NoMatch => "No Match",
        _ => "Unmatched",
    };
}
