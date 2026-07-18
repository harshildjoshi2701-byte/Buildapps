using System.Globalization;
using System.Text;
using BankReconciliation.Core.Models;

namespace BankReconciliation.Core.Services;

/// <summary>
/// Writes a lightweight CSV summary of everything that still needs
/// attention after a run — No Match, Manual Review, and Possible Duplicate
/// rows from both sheets — so a user can share just the follow-up items
/// (e.g. by email) without sending the full reconciled workbook.
/// Deliberately plain CSV, not another .xlsx: no formatting/formula
/// concerns, and every spreadsheet tool opens a CSV without this codebase
/// needing ClosedXML on a second write path. A pure static function, same
/// pattern as the Matching namespace's stateless matchers — nothing here
/// needs mocking in a test, so there's no interface to go with it.
/// </summary>
public static class ReportExporter
{
    /// <summary>Writes the report next to <paramref name="sourceFilePath"/>
    /// as "&lt;name&gt;_NeedsReview.csv", auto-incrementing if that name is
    /// already taken (same convention as <see cref="IExcelService.BuildOutputPath"/>).
    /// Returns the path written to.</summary>
    public static string ExportUnmatchedReport(
        IReadOnlyList<TransactionRecord> bankTransactions,
        IReadOnlyList<TransactionRecord> r365Transactions,
        string sourceFilePath)
    {
        var rows = bankTransactions.Concat(r365Transactions)
            .Where(NeedsAttention)
            .OrderBy(t => t.Status == MatchStatus.NoMatch ? 1 : 0) // Manual Review / Possible Duplicate first — usually closer to resolved
            .ThenBy(t => t.Side)
            .ThenBy(t => t.RowNumber);

        var sb = new StringBuilder();
        sb.Append("Side,Row,Date,Amount,Grouping,Description,Status,Comment\r\n");
        foreach (var t in rows)
        {
            sb.Append(string.Join(",",
                CsvField(t.Side.ToString()),
                CsvField(t.RowNumber.ToString(CultureInfo.InvariantCulture)),
                CsvField(t.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                CsvField(t.AmountDollars.ToString("F2", CultureInfo.InvariantCulture)),
                CsvField(t.GroupingKey),
                CsvField(t.DescriptionSnippet),
                CsvField(t.Status.ToString()),
                CsvField(t.Comment)));
            sb.Append("\r\n");
        }

        var outputPath = BuildReportPath(sourceFilePath);
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(outputPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)); // BOM so Excel auto-detects UTF-8 on double-click open

        return outputPath;
    }

    private static bool NeedsAttention(TransactionRecord t) =>
        t.Status is MatchStatus.NoMatch or MatchStatus.ManualReview or MatchStatus.PossibleDuplicate;

    private static string BuildReportPath(string sourceFilePath)
    {
        var directory = Path.GetDirectoryName(sourceFilePath);
        if (string.IsNullOrEmpty(directory)) directory = ".";
        var nameNoExt = Path.GetFileNameWithoutExtension(sourceFilePath);

        var candidate = Path.Combine(directory, $"{nameNoExt}_NeedsReview.csv");
        var attempt = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"{nameNoExt}_NeedsReview ({attempt}).csv");
            attempt++;
        }
        return candidate;
    }

    private static string CsvField(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var needsQuoting = value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        return needsQuoting ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
}
