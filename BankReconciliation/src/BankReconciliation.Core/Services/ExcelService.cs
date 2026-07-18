using BankReconciliation.Core.Matching;
using BankReconciliation.Core.Models;
using ClosedXML.Excel;

namespace BankReconciliation.Core.Services;

/// <summary>
/// ClosedXML-based implementation of <see cref="IExcelService"/>.
///
/// ClosedXML (MIT licensed, no commercial-use fee — see README "Architecture
/// Decisions" for why this was chosen over EPPlus) loads the workbook into a
/// full in-memory object model that preserves everything it isn't explicitly
/// told to change: formulas, cell styles, fonts, borders, merged cells,
/// hidden rows/columns, autofilters, column widths, and worksheet names all
/// survive a Load -> WriteResultsAndSave round trip untouched, because this
/// class never creates a new workbook — it opens the original, mutates only
/// the specific cells the reconciliation result requires, and saves that same
/// object graph to a new path.
/// </summary>
public sealed class ExcelService : IExcelService
{
    /// <summary>How many columns (1..N) are captured into every row's
    /// <see cref="TransactionRecord.RawColumns"/>. Wide enough to cover any
    /// reasonable custom-matching-rule column choice on either side of the
    /// sheet (the shipped template only uses columns through AB/28) without
    /// needing to know the mapping's exact column set in advance.</summary>
    private const int RawColumnCaptureWidth = 40;

    public LoadedWorkbook Load(string filePath, ColumnMapping mapping, bool ignoreAlreadyReconciledRows)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Workbook not found: {filePath}", filePath);

        var workbook = new XLWorkbook(filePath);
        var worksheet = string.IsNullOrWhiteSpace(mapping.WorksheetName)
            ? workbook.Worksheets.First()
            : workbook.Worksheet(mapping.WorksheetName);

        var lastUsedRow = worksheet.LastRowUsed()?.RowNumber() ?? mapping.BankDataStartRow;

        var bankTransactions = ReadBankTransactions(worksheet, mapping, lastUsedRow, ignoreAlreadyReconciledRows);
        var r365Transactions = ReadR365Transactions(worksheet, mapping, lastUsedRow, ignoreAlreadyReconciledRows);

        return new LoadedWorkbook
        {
            Workbook = workbook,
            Worksheet = worksheet,
            BankTransactions = bankTransactions,
            R365Transactions = r365Transactions,
            SourceFilePath = filePath,
        };
    }

    private static List<TransactionRecord> ReadBankTransactions(IXLWorksheet ws, ColumnMapping map, int lastUsedRow, bool ignoreAlreadyReconciled)
    {
        var results = new List<TransactionRecord>();
        var consecutiveBlankRows = 0;

        for (int row = map.BankDataStartRow; row <= lastUsedRow; row++)
        {
            var dateCell = ws.Cell(row, map.BankDateColumn);
            if (dateCell.IsEmpty() || dateCell.DataType != XLDataType.DateTime)
            {
                consecutiveBlankRows++;
                if (consecutiveBlankRows > 10) break; // genuinely past the end of the block
                continue;
            }
            consecutiveBlankRows = 0;

            var date = dateCell.GetDateTime();
            var credit = ReadDoubleOrZero(ws.Cell(row, map.BankCreditColumn));
            var debit = ReadDoubleOrZero(ws.Cell(row, map.BankDebitColumn));
            var amountCents = MoneyMath.ToCents((decimal)credit) - MoneyMath.ToCents((decimal)debit);
            var description = ReadStringOrEmpty(ws.Cell(row, map.BankDescriptionColumn));

            var existingComment = ReadStringOrEmpty(ws.Cell(row, map.BankCommentColumn));
            var preLocked = ignoreAlreadyReconciled && !string.IsNullOrWhiteSpace(existingComment);

            results.Add(new TransactionRecord
            {
                RowNumber = row,
                Side = TransactionSide.Bank,
                Date = date.Date,
                AmountCents = amountCents,
                DescriptionSnippet = Truncate(description, 80),
                RawColumns = ReadRawColumns(ws, row),
                IsMatched = preLocked,
                IsPreLocked = preLocked,
                Status = preLocked ? MatchStatus.MatchedExact : MatchStatus.Unmatched,
                Comment = preLocked ? existingComment : string.Empty,
            });
        }
        return results;
    }

    private static List<TransactionRecord> ReadR365Transactions(IXLWorksheet ws, ColumnMapping map, int lastUsedRow, bool ignoreAlreadyReconciled)
    {
        var results = new List<TransactionRecord>();
        var consecutiveBlankRows = 0;

        for (int row = map.R365DataStartRow; row <= lastUsedRow; row++)
        {
            var dateCell = ws.Cell(row, map.R365DateColumn);
            var amountCell = ws.Cell(row, map.R365AmountColumn);
            if (dateCell.IsEmpty() || dateCell.DataType != XLDataType.DateTime || amountCell.IsEmpty())
            {
                consecutiveBlankRows++;
                if (consecutiveBlankRows > 10) break;
                continue;
            }
            consecutiveBlankRows = 0;

            var date = dateCell.GetDateTime();
            var amount = ReadDoubleOrZero(amountCell);
            var amountCents = MoneyMath.ToCents((decimal)amount);
            var reference = ReadStringOrEmpty(ws.Cell(row, map.R365ReferenceColumn));
            var description = ReadStringOrEmpty(ws.Cell(row, map.R365DescriptionColumn));
            var isGrouped = reference.Contains(map.GroupKeyword, StringComparison.OrdinalIgnoreCase);

            var existingComment = ReadStringOrEmpty(ws.Cell(row, map.R365CommentColumn));
            var preLocked = ignoreAlreadyReconciled && !string.IsNullOrWhiteSpace(existingComment);

            results.Add(new TransactionRecord
            {
                RowNumber = row,
                Side = TransactionSide.R365,
                Date = date.Date,
                AmountCents = amountCents,
                Reference = reference,
                IsGroupedPosting = isGrouped,
                DescriptionSnippet = Truncate(description, 80),
                RawColumns = ReadRawColumns(ws, row),
                IsMatched = preLocked,
                IsPreLocked = preLocked,
                Status = preLocked ? MatchStatus.MatchedExact : MatchStatus.Unmatched,
                Comment = preLocked ? existingComment : string.Empty,
            });
        }
        return results;
    }

    public void WriteResultsAndSave(
        LoadedWorkbook loaded,
        IReadOnlyList<TransactionRecord> bankTransactions,
        IReadOnlyList<TransactionRecord> r365Transactions,
        HighlightColors colors,
        ColumnMapping mapping,
        bool writeConfidenceColumn,
        bool highlightFullRow,
        bool highlightOnlyUnmatched,
        bool tidyColumnsOnOutput,
        string outputFilePath)
    {
        var ws = loaded.Worksheet;

        // Column letters used to build the K / AB helper formulas below.
        var creditLetter = ColumnLetter(mapping.BankCreditColumn);
        var debitLetter = ColumnLetter(mapping.BankDebitColumn);
        var bankMatchIdLetter = ColumnLetter(mapping.BankConfidenceColumn);
        var r365MatchIdLetter = ColumnLetter(mapping.R365ConfidenceColumn);
        var bankDiffLetter = ColumnLetter(mapping.BankDiffColumn);
        var r365AmountLetter = ColumnLetter(mapping.R365AmountColumn);

        foreach (var t in bankTransactions)
        {
            WriteOne(ws, t, mapping.BankCommentColumn, mapping.BankConfidenceColumn,
                     mapping.BankFirstColumn, mapping.BankCommentColumn,
                     colors, writeConfidenceColumn, highlightFullRow, highlightOnlyUnmatched,
                     extra: !writeConfidenceColumn ? null : tt =>
                     {
                         // K = Credit - Debit, a signed net amount consistent
                         // with R365's signed Amount column convention. Written
                         // for every bank row (not just matched ones) — it's
                         // useful as a plain reference value and is what the
                         // AB difference-check formula on the R365 side sums.
                         ws.Cell(tt.RowNumber, mapping.BankDiffColumn).FormulaA1 =
                             $"{creditLetter}{tt.RowNumber}-{debitLetter}{tt.RowNumber}";
                     });
        }

        foreach (var t in r365Transactions)
        {
            WriteOne(ws, t, mapping.R365CommentColumn, mapping.R365ConfidenceColumn,
                     mapping.R365FirstColumn, mapping.R365CommentColumn,
                     colors, writeConfidenceColumn, highlightFullRow, highlightOnlyUnmatched,
                     extra: (!writeConfidenceColumn || t.GroupId < 0) ? null : tt =>
                     {
                         // AB = (sum of K for every Bank row sharing this row's
                         // Match ID) - (sum of Y for every R365 row sharing this
                         // row's Match ID). Zero means the match group's bank
                         // side and R365 side agree exactly; anything else
                         // flags a discrepancy worth a second look. Only
                         // written for matched rows (GroupId >= 0) — leaving it
                         // blank for unmatched rows avoids SUMIF matching on a
                         // blank criteria cell, which would sum unrelated rows.
                         ws.Cell(tt.RowNumber, mapping.R365DiffColumn).FormulaA1 =
                             $"SUMIF({bankMatchIdLetter}:{bankMatchIdLetter},{r365MatchIdLetter}{tt.RowNumber},{bankDiffLetter}:{bankDiffLetter})" +
                             $"-SUMIF({r365MatchIdLetter}:{r365MatchIdLetter},{r365MatchIdLetter}{tt.RowNumber},{r365AmountLetter}:{r365AmountLetter})";
                     });
        }

        // Header labels — only set if currently blank, so a re-run never
        // stomps on a user's own header customization.
        SetHeaderIfBlank(ws, mapping.BankHeaderRow, mapping.BankCommentColumn, "Reconciliation Comment");
        if (writeConfidenceColumn)
        {
            SetHeaderIfBlank(ws, mapping.BankHeaderRow, mapping.BankConfidenceColumn, "Match ID");
            SetHeaderIfBlank(ws, mapping.BankHeaderRow, mapping.BankDiffColumn, "Net (Credit-Debit)");
        }
        SetHeaderIfBlank(ws, mapping.R365HeaderRow, mapping.R365CommentColumn, "Reconciliation Comment");
        if (writeConfidenceColumn)
        {
            SetHeaderIfBlank(ws, mapping.R365HeaderRow, mapping.R365ConfidenceColumn, "Match ID");
            SetHeaderIfBlank(ws, mapping.R365HeaderRow, mapping.R365DiffColumn, "Match Difference");
        }

        if (tidyColumnsOnOutput) TidyColumns(ws, mapping);

        var directory = Path.GetDirectoryName(outputFilePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        loaded.Workbook.SaveAs(outputFilePath);
    }

    private static void WriteOne(
        IXLWorksheet ws, TransactionRecord t,
        int commentColumn, int matchIdColumn,
        int highlightFirstColumn, int highlightLastColumn,
        HighlightColors colors, bool writeMatchId, bool highlightFullRow, bool highlightOnlyUnmatched,
        Action<TransactionRecord>? extra = null)
    {
        if (t.IsPreLocked) return; // leave rows the engine never touched exactly as they were

        ws.Cell(t.RowNumber, commentColumn).Value = t.Comment;

        // Match ID: the same number is written on the bank row and every R365
        // row it matched with (1:1 or combination), so sorting either block by
        // this column puts a match's transactions side by side. Left blank for
        // anything not actually matched (GroupId is -1 for those).
        if (writeMatchId && t.GroupId >= 0)
        {
            var matchIdCell = ws.Cell(t.RowNumber, matchIdColumn);
            matchIdCell.Value = t.GroupId + 1; // 1-based purely for readability
            matchIdCell.Style.NumberFormat.Format = "0";
        }

        extra?.Invoke(t);

        var hex = ColorFor(t, colors, highlightOnlyUnmatched);
        if (hex is null) return;

        var color = XLColor.FromHtml(hex);
        if (highlightFullRow)
        {
            ws.Range(t.RowNumber, highlightFirstColumn, t.RowNumber, highlightLastColumn).Style.Fill.BackgroundColor = color;
        }
        else
        {
            ws.Cell(t.RowNumber, commentColumn).Style.Fill.BackgroundColor = color;
        }
    }

    private static string? ColorFor(TransactionRecord t, HighlightColors colors, bool highlightOnlyUnmatched)
    {
        if (highlightOnlyUnmatched)
        {
            // Cleaner sheet: only rows that still need attention get a fill
            // color. Matched/Manual Review/Combination rows are left
            // uncolored so the real problems stand out at a glance.
            return t.Status == MatchStatus.NoMatch ? colors.NoMatchHex : null;
        }

        return t.Status switch
        {
            MatchStatus.MatchedExact or MatchStatus.MatchedDateTolerant => colors.MatchedHex,
            MatchStatus.MatchedCombination => colors.ColorForGroup(t.GroupId),
            MatchStatus.ManualReview or MatchStatus.PossibleDuplicate => colors.ManualReviewHex,
            MatchStatus.NoMatch => colors.NoMatchHex,
            _ => null,
        };
    }

    private static void SetHeaderIfBlank(IXLWorksheet ws, int headerRow, int column, string label)
    {
        var cell = ws.Cell(headerRow, column);
        if (cell.IsEmpty()) cell.Value = label;
    }

    /// <summary>Hides the blank spacer columns between the end of the Bank
    /// block and the start of the R365 block, and autofits every used
    /// column's width based only on the header row (row 2) content.</summary>
    private static void TidyColumns(IXLWorksheet ws, ColumnMapping mapping)
    {
        var bankLastColumn = new[] { mapping.BankCommentColumn, mapping.BankConfidenceColumn, mapping.BankDiffColumn }.Max();
        var gapStart = bankLastColumn + 1;
        var gapEnd = mapping.R365FirstColumn - 1;
        if (gapEnd >= gapStart)
            ws.Columns(gapStart, gapEnd).Hide();

        ws.Columns().AdjustToContents(mapping.BankHeaderRow, mapping.BankHeaderRow);
    }

    /// <summary>Converts a 1-based Excel column number to its letter form
    /// (1 -> "A", 27 -> "AA", 28 -> "AB", ...).</summary>
    private static string ColumnLetter(int column)
    {
        var letters = string.Empty;
        while (column > 0)
        {
            var remainder = (column - 1) % 26;
            letters = (char)('A' + remainder) + letters;
            column = (column - 1) / 26;
        }
        return letters;
    }

    public string BuildOutputPath(string sourceFilePath)
    {
        var directory = Path.GetDirectoryName(sourceFilePath) ?? ".";
        var nameNoExt = Path.GetFileNameWithoutExtension(sourceFilePath);
        var ext = Path.GetExtension(sourceFilePath);
        if (string.IsNullOrEmpty(ext)) ext = ".xlsx";

        var candidate = Path.Combine(directory, $"{nameNoExt}_Reconciled{ext}");
        var attempt = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"{nameNoExt}_Reconciled ({attempt}){ext}");
            attempt++;
        }
        return candidate;
    }

    private static double ReadDoubleOrZero(IXLCell cell)
    {
        if (cell.IsEmpty()) return 0.0;
        return cell.TryGetValue(out double value) ? value : 0.0;
    }

    private static string ReadStringOrEmpty(IXLCell cell)
    {
        if (cell.IsEmpty()) return string.Empty;
        return cell.TryGetValue(out string value) ? value : cell.GetFormattedString();
    }

    /// <summary>Reads columns 1..<see cref="RawColumnCaptureWidth"/> of the
    /// given row as plain text, for <see cref="TransactionRecord.RawColumns"/>.
    /// Blank cells are simply omitted rather than stored as empty strings —
    /// callers already treat a missing key as "".</summary>
    private static Dictionary<int, string> ReadRawColumns(IXLWorksheet ws, int row)
    {
        var raw = new Dictionary<int, string>(RawColumnCaptureWidth);
        for (int col = 1; col <= RawColumnCaptureWidth; col++)
        {
            var cell = ws.Cell(row, col);
            if (cell.IsEmpty()) continue;
            raw[col] = ReadStringOrEmpty(cell);
        }
        return raw;
    }

    private static string Truncate(string s, int maxLength) =>
        string.IsNullOrEmpty(s) || s.Length <= maxLength ? s : s[..maxLength];
}
