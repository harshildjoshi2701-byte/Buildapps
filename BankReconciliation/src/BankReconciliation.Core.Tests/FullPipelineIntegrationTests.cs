using BankReconciliation.Core.Matching;
using BankReconciliation.Core.Models;
using BankReconciliation.Core.Services;
using ClosedXML.Excel;
using Xunit;

namespace BankReconciliation.Core.Tests;

/// <summary>
/// End-to-end test mirroring the real-world scenario the Grouping-column
/// work was built against: a numeric linking ID that ties out, one that
/// doesn't, one that's R365-only, a "Sysco" named-rule keyword living in the
/// same Grouping column, and a plain ungrouped exact match — all driven
/// through the real Load -> RunAsync -> WriteResultsAndSave pipeline against
/// an actual .xlsx on disk, not individual units in isolation.
/// </summary>
public class FullPipelineIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public FullPipelineIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FullPipelineTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task FullPipeline_RealWorldGroupingScenario_ProducesExpectedOutcomeForEveryCase()
    {
        var mapping = new ColumnMapping
        {
            BankWorksheetName = "Bank Transactions",
            R365WorksheetName = "R365 Transactions",
            BankHeaderRow = 2, BankDataStartRow = 3, BankFirstColumn = 1,
            BankDateColumn = 3, BankCreditColumn = 6, BankDebitColumn = 7,
            BankGroupingColumn = 8, BankDescriptionColumn = 8,
            BankCommentColumn = 9, BankConfidenceColumn = 10, BankDiffColumn = 11,
            R365HeaderRow = 2, R365DataStartRow = 3, R365FirstColumn = 1,
            R365DateColumn = 1, R365GroupingColumn = 2, R365ReferenceColumn = 3,
            R365DescriptionColumn = 4, R365AmountColumn = 5,
            R365CommentColumn = 6, R365ConfidenceColumn = 7, R365DiffColumn = 8,
        };
        // Default SpecialComboRules already targets BankColumn=8/"Sysco" and
        // R365Column=2/"Sysco" — exactly this mapping's Grouping columns.
        var settings = new ReconciliationSettings { Columns = mapping, MaxThreads = 1 };

        using var wb = new XLWorkbook();
        var bankWs = wb.Worksheets.Add("Bank Transactions");
        var r365Ws = wb.Worksheets.Add("R365 Transactions");
        var baseDate = new DateTime(2026, 6, 1);
        int bankRow = 3, r365Row = 3;

        // Case 1: numeric Grouping ID that ties out exactly (1 bank vs 3 R365).
        bankWs.Cell(bankRow, 3).Value = baseDate; bankWs.Cell(bankRow, 6).Value = 600.00; bankWs.Cell(bankRow, 8).Value = "10";
        bankRow++;
        foreach (var amt in new[] { 100.00, 200.00, 300.00 })
        {
            r365Ws.Cell(r365Row, 1).Value = baseDate.AddDays(-2); r365Ws.Cell(r365Row, 5).Value = amt; r365Ws.Cell(r365Row, 2).Value = "10";
            r365Row++;
        }

        // Case 2: numeric Grouping ID that does NOT tie out (real variance shape).
        bankWs.Cell(bankRow, 3).Value = baseDate; bankWs.Cell(bankRow, 6).Value = 949652.13; bankWs.Cell(bankRow, 8).Value = "6";
        bankRow++;
        r365Ws.Cell(r365Row, 1).Value = baseDate.AddDays(-1); r365Ws.Cell(r365Row, 5).Value = 947882.58; r365Ws.Cell(r365Row, 2).Value = "6";
        r365Row++;

        // Case 3: Grouping ID only on the R365 side (one-sided, no Bank counterpart).
        foreach (var amt in new[] { 50.00, 75.00 })
        {
            r365Ws.Cell(r365Row, 1).Value = baseDate.AddDays(-3); r365Ws.Cell(r365Row, 5).Value = amt; r365Ws.Cell(r365Row, 2).Value = "99";
            r365Row++;
        }

        // Case 4: Sysco named rule — keyword in the SAME Grouping column, deliberately does not sum-match.
        bankWs.Cell(bankRow, 3).Value = baseDate; bankWs.Cell(bankRow, 6).Value = 180.00; bankWs.Cell(bankRow, 8).Value = "Sysco";
        bankRow++;
        r365Ws.Cell(r365Row, 1).Value = baseDate.AddDays(-5); r365Ws.Cell(r365Row, 5).Value = 110.00; r365Ws.Cell(r365Row, 2).Value = "Sysco";
        r365Row++;

        // Case 5: plain ungrouped exact match (blank Grouping cell).
        bankWs.Cell(bankRow, 3).Value = baseDate; bankWs.Cell(bankRow, 6).Value = 42.50;
        r365Ws.Cell(r365Row, 1).Value = baseDate; r365Ws.Cell(r365Row, 5).Value = 42.50;

        var sourcePath = Path.Combine(_tempDir, "Scenario.xlsx");
        wb.SaveAs(sourcePath);

        var excelService = new ExcelService();
        var loaded = excelService.Load(sourcePath, mapping, ignoreAlreadyReconciledRows: false);

        await new ReconciliationEngine().RunAsync(loaded.BankTransactions, loaded.R365Transactions, settings);

        var group10Bank = loaded.BankTransactions.Single(t => t.GroupingKey == "10");
        Assert.Equal(MatchStatus.MatchedCombination, group10Bank.Status);
        Assert.All(loaded.R365Transactions.Where(t => t.GroupingKey == "10"), t => Assert.Equal(MatchStatus.MatchedCombination, t.Status));

        var group6Bank = loaded.BankTransactions.Single(t => t.GroupingKey == "6");
        Assert.Equal(MatchStatus.ManualReview, group6Bank.Status);
        Assert.Contains("differs by", group6Bank.Comment);

        var group99Rows = loaded.R365Transactions.Where(t => t.GroupingKey == "99").ToList();
        Assert.Equal(2, group99Rows.Count);
        Assert.All(group99Rows, t => Assert.Equal(MatchStatus.NoMatch, t.Status));
        Assert.Equal(group99Rows[0].GroupId, group99Rows[1].GroupId);

        var syscoBank = loaded.BankTransactions.Single(t => t.GroupingKey == "Sysco");
        Assert.Equal(MatchStatus.MatchedCombination, syscoBank.Status);
        Assert.Contains("Named Rule", syscoBank.Comment);

        var plainBank = loaded.BankTransactions.Single(t => string.IsNullOrEmpty(t.GroupingKey));
        Assert.Equal(MatchStatus.MatchedExact, plainBank.Status);

        // Every transaction on both sheets must end in a terminal (non-Unmatched) status.
        Assert.All(loaded.BankTransactions, t => Assert.NotEqual(MatchStatus.Unmatched, t.Status));
        Assert.All(loaded.R365Transactions, t => Assert.NotEqual(MatchStatus.Unmatched, t.Status));

        // Round-trip through the real write path and back, not just in-memory state.
        var outputPath = excelService.BuildOutputPath(sourcePath);
        excelService.WriteResultsAndSave(loaded, loaded.BankTransactions, loaded.R365Transactions,
            settings.Colors, mapping, settings.WriteConfidenceColumn, settings.HighlightFullRow,
            settings.HighlightOnlyUnmatched, settings.TidyColumnsOnOutput, outputPath);
        loaded.Dispose();

        using var outputWb = new XLWorkbook(outputPath);
        var outputBankWs = outputWb.Worksheet("Bank Transactions");
        var outputR365Ws = outputWb.Worksheet("R365 Transactions");

        // Bank rows, in write order: 3=group10, 4=group6, 5=Sysco, 6=plain.
        Assert.False(outputBankWs.Cell(3, 10).IsEmpty()); // group-10 bank row got a Match ID
        Assert.Contains("Grouping", outputBankWs.Cell(4, 9).GetString()); // group-6 bank row's comment mentions Grouping
        Assert.Contains("Named Rule", outputBankWs.Cell(5, 9).GetString()); // Sysco bank row
        Assert.Equal("Matched (Exact)", outputBankWs.Cell(6, 9).GetString()); // plain exact-match bank row
        // R365 rows, in write order: 3-5=group10, 6=group6, 7-8=group99, 9=Sysco, 10=plain.
        Assert.False(outputR365Ws.Cell(7, 6).IsEmpty()); // first group-99 R365 row got a comment written
    }
}
