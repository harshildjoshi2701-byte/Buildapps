using BankReconciliation.Core.Models;
using BankReconciliation.Core.Services;
using Xunit;
using static BankReconciliation.Core.Tests.TestHelpers;

namespace BankReconciliation.Core.Tests;

public class ReportExporterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _sourceFilePath;

    public ReportExporterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ReportExporterTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _sourceFilePath = Path.Combine(_tempDir, "Bank Reconciliation.xlsx");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void ExportUnmatchedReport_OnlyIncludesRowsNeedingAttention()
    {
        var matched = Bank(1, 10, -500.00m);
        matched.Status = MatchStatus.MatchedExact;
        var noMatch = Bank(2, 10, -300.00m);
        noMatch.Status = MatchStatus.NoMatch;
        var manualReview = R365(3, 9, -100.00m);
        manualReview.Status = MatchStatus.ManualReview;

        var path = ReportExporter.ExportUnmatchedReport(new[] { matched, noMatch }, new[] { manualReview }, _sourceFilePath);
        var lines = File.ReadAllLines(path);

        Assert.Equal(3, lines.Length); // header + noMatch + manualReview
        Assert.DoesNotContain(lines, l => l.Contains("-500.00"));
        Assert.Contains(lines, l => l.Contains("-300.00"));
        Assert.Contains(lines, l => l.Contains("-100.00"));
    }

    [Fact]
    public void ExportUnmatchedReport_NoRowsNeedAttention_WritesHeaderOnly()
    {
        var matched = Bank(1, 10, -500.00m);
        matched.Status = MatchStatus.MatchedExact;

        var path = ReportExporter.ExportUnmatchedReport(new[] { matched }, Array.Empty<TransactionRecord>(), _sourceFilePath);
        var lines = File.ReadAllLines(path);

        Assert.Single(lines);
        Assert.StartsWith("Side,Row,Date,Amount,Grouping,Description,Status,Comment", lines[0]);
    }

    [Fact]
    public void ExportUnmatchedReport_WritesNextToSourceFile_WithNeedsReviewSuffix()
    {
        var noMatch = Bank(1, 10, -300.00m);
        noMatch.Status = MatchStatus.NoMatch;

        var path = ReportExporter.ExportUnmatchedReport(new[] { noMatch }, Array.Empty<TransactionRecord>(), _sourceFilePath);

        Assert.Equal(Path.Combine(_tempDir, "Bank Reconciliation_NeedsReview.csv"), path);
    }

    [Fact]
    public void ExportUnmatchedReport_ExistingFile_AutoIncrementsRatherThanOverwriting()
    {
        var noMatch = Bank(1, 10, -300.00m);
        noMatch.Status = MatchStatus.NoMatch;

        var first = ReportExporter.ExportUnmatchedReport(new[] { noMatch }, Array.Empty<TransactionRecord>(), _sourceFilePath);
        var second = ReportExporter.ExportUnmatchedReport(new[] { noMatch }, Array.Empty<TransactionRecord>(), _sourceFilePath);

        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public void ExportUnmatchedReport_CommentContainingCommaAndQuote_IsProperlyCsvEscaped()
    {
        var noMatch = Bank(1, 10, -300.00m);
        noMatch.Status = MatchStatus.NoMatch;
        noMatch.Comment = "Manual Review (Grouping \"6\" — differs by $1,769.55)";

        var path = ReportExporter.ExportUnmatchedReport(new[] { noMatch }, Array.Empty<TransactionRecord>(), _sourceFilePath);
        var lines = File.ReadAllLines(path);

        // The escaped field must be a single quoted CSV field, not split across
        // extra columns by its embedded comma.
        Assert.Contains(lines, l => l.Contains("\"Manual Review (Grouping \"\"6\"\" — differs by $1,769.55)\""));
    }

    [Fact]
    public void ExportUnmatchedReport_ManualReviewAndPossibleDuplicate_SortBeforeNoMatch()
    {
        var noMatch = Bank(1, 10, -300.00m);
        noMatch.Status = MatchStatus.NoMatch;
        var manualReview = Bank(2, 10, -400.00m);
        manualReview.Status = MatchStatus.ManualReview;

        var path = ReportExporter.ExportUnmatchedReport(new[] { noMatch, manualReview }, Array.Empty<TransactionRecord>(), _sourceFilePath);
        var lines = File.ReadAllLines(path);

        var manualReviewLineIndex = Array.FindIndex(lines, l => l.Contains("-400.00"));
        var noMatchLineIndex = Array.FindIndex(lines, l => l.Contains("-300.00"));
        Assert.True(manualReviewLineIndex < noMatchLineIndex);
    }
}
