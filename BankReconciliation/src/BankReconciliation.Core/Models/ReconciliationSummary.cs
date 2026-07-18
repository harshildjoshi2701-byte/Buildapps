namespace BankReconciliation.Core.Models;

/// <summary>
/// Aggregate statistics for one reconciliation run — backs the Summary
/// Dashboard in the UI and the run header in the log file.
/// </summary>
public sealed class ReconciliationSummary
{
    public int TotalBankTransactions { get; set; }
    public int TotalR365Transactions { get; set; }

    public int MatchedBankTransactions { get; set; }
    public int MatchedR365Transactions { get; set; }

    public decimal MatchedAmount { get; set; }

    public int UnmatchedBankTransactions { get; set; }
    public int UnmatchedR365Transactions { get; set; }

    public int OneToOneMatches { get; set; }
    public int CombinationMatches { get; set; }
    public int ManualReviewCount { get; set; }
    public int PossibleDuplicateBankCount { get; set; }
    public int PossibleDuplicateR365Count { get; set; }

    public double BankMatchPercentage =>
        TotalBankTransactions == 0 ? 0 : 100.0 * MatchedBankTransactions / TotalBankTransactions;

    public double R365MatchPercentage =>
        TotalR365Transactions == 0 ? 0 : 100.0 * MatchedR365Transactions / TotalR365Transactions;

    public TimeSpan ProcessingTime { get; set; }
    public DateTime RunTimestamp { get; set; } = DateTime.Now;
    public string SourceFilePath { get; set; } = string.Empty;
    public string OutputFilePath { get; set; } = string.Empty;
    public string LogFilePath { get; set; } = string.Empty;

    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
}
