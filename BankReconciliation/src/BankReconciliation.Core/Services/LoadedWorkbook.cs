using BankReconciliation.Core.Models;
using ClosedXML.Excel;

namespace BankReconciliation.Core.Services;

/// <summary>
/// Holds an open ClosedXML workbook plus the transactions already parsed out
/// of it. Keeping the workbook object alive between Load and
/// WriteResultsAndSave is what allows the write step to touch only the cells
/// it needs to and leave everything else byte-for-byte as it was loaded.
/// </summary>
public sealed class LoadedWorkbook : IDisposable
{
    public required XLWorkbook Workbook { get; init; }
    public required IXLWorksheet Worksheet { get; init; }
    public required List<TransactionRecord> BankTransactions { get; init; }
    public required List<TransactionRecord> R365Transactions { get; init; }
    public required string SourceFilePath { get; init; }

    public void Dispose() => Workbook.Dispose();
}
