using BankReconciliation.Core.Models;

namespace BankReconciliation.Core.Services;

/// <summary>
/// Reads a Bank Reconciliation workbook into plain <see cref="TransactionRecord"/>
/// lists and writes reconciliation results back into a NEW copy of that
/// workbook — the original file on disk is never opened for writing and
/// never modified. See <see cref="ExcelService"/> for the ClosedXML-based
/// implementation and exactly what is/isn't touched on write.
/// </summary>
public interface IExcelService
{
    /// <summary>
    /// Opens <paramref name="filePath"/> and parses both transaction blocks
    /// according to <paramref name="mapping"/>. The returned
    /// <see cref="LoadedWorkbook"/> keeps the underlying workbook open in
    /// memory (dispose it when done) so <see cref="WriteResultsAndSave"/> can
    /// write into the exact same in-memory object graph — this is what lets
    /// every existing format, formula, merged cell, hidden row/column, filter,
    /// and column width survive untouched.
    /// </summary>
    LoadedWorkbook Load(string filePath, ColumnMapping mapping, bool ignoreAlreadyReconciledRows);

    /// <summary>
    /// Writes each transaction's comment, confidence, and highlight color into
    /// the loaded workbook and saves it to <paramref name="outputFilePath"/>.
    /// Only ever touches: the comment column, the confidence column (if
    /// enabled), and the fill color of the transaction's row range. Formulas,
    /// every other cell's value, and all formatting elsewhere are left exactly
    /// as they were read.
    /// </summary>
    /// <remarks><paramref name="mapping"/> should be the exact same
    /// <see cref="ColumnMapping"/> instance used for <see cref="Load"/> — it
    /// determines which columns are written to and highlighted.</remarks>
    void WriteResultsAndSave(
        LoadedWorkbook loaded,
        IReadOnlyList<TransactionRecord> bankTransactions,
        IReadOnlyList<TransactionRecord> r365Transactions,
        HighlightColors colors,
        ColumnMapping mapping,
        bool writeConfidenceColumn,
        bool highlightFullRow,
        bool highlightOnlyUnmatched,
        bool tidyColumnsOnOutput,
        string outputFilePath);

    /// <summary>
    /// Builds the "never overwrite the source" output path:
    /// "MyFile.xlsx" -> "MyFile_Reconciled.xlsx", auto-incrementing
    /// ("MyFile_Reconciled (2).xlsx", ...) if that name is already taken.
    /// </summary>
    string BuildOutputPath(string sourceFilePath);
}
