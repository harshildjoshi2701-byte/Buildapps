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
        // Real confirmed layout (see ColumnMapping remarks) rather than a
        // hand-picked one: headers on row 4, data from row 5, Grouping at
        // Bank column 5 / R365 column 4. Default SpecialComboRules targets
        // BankColumn=5/"Sysco" and R365Column=4/"Sysco" — exactly these.
        var settings = new ReconciliationSettings { MaxThreads = 1 };
        var mapping = settings.Columns;

        using var wb = new XLWorkbook();
        var bankWs = wb.Worksheets.Add("Bank Transactions");
        var r365Ws = wb.Worksheets.Add("R365 Transactions");
        var baseDate = new DateTime(2026, 6, 1);
        int bankRow = mapping.BankDataStartRow, r365Row = mapping.R365DataStartRow;

        // Case 1: numeric Grouping ID that ties out exactly (1 bank vs 3 R365).
        bankWs.Cell(bankRow, mapping.BankDateColumn).Value = baseDate; bankWs.Cell(bankRow, mapping.BankCreditColumn).Value = 600.00; bankWs.Cell(bankRow, mapping.BankGroupingColumn).Value = "10";
        bankRow++;
        foreach (var amt in new[] { 100.00, 200.00, 300.00 })
        {
            r365Ws.Cell(r365Row, mapping.R365DateColumn).Value = baseDate.AddDays(-2); r365Ws.Cell(r365Row, mapping.R365AmountColumn).Value = amt; r365Ws.Cell(r365Row, mapping.R365GroupingColumn).Value = "10";
            r365Row++;
        }

        // Case 2: numeric Grouping ID that does NOT tie out (real variance shape).
        bankWs.Cell(bankRow, mapping.BankDateColumn).Value = baseDate; bankWs.Cell(bankRow, mapping.BankCreditColumn).Value = 949652.13; bankWs.Cell(bankRow, mapping.BankGroupingColumn).Value = "6";
        bankRow++;
        r365Ws.Cell(r365Row, mapping.R365DateColumn).Value = baseDate.AddDays(-1); r365Ws.Cell(r365Row, mapping.R365AmountColumn).Value = 947882.58; r365Ws.Cell(r365Row, mapping.R365GroupingColumn).Value = "6";
        r365Row++;

        // Case 3: Grouping ID only on the R365 side (one-sided, no Bank counterpart).
        foreach (var amt in new[] { 50.00, 75.00 })
        {
            r365Ws.Cell(r365Row, mapping.R365DateColumn).Value = baseDate.AddDays(-3); r365Ws.Cell(r365Row, mapping.R365AmountColumn).Value = amt; r365Ws.Cell(r365Row, mapping.R365GroupingColumn).Value = "99";
            r365Row++;
        }

        // Case 4: Sysco named rule — keyword in the SAME Grouping column, deliberately does not sum-match.
        bankWs.Cell(bankRow, mapping.BankDateColumn).Value = baseDate; bankWs.Cell(bankRow, mapping.BankCreditColumn).Value = 180.00; bankWs.Cell(bankRow, mapping.BankGroupingColumn).Value = "Sysco";
        bankRow++;
        r365Ws.Cell(r365Row, mapping.R365DateColumn).Value = baseDate.AddDays(-5); r365Ws.Cell(r365Row, mapping.R365AmountColumn).Value = 110.00; r365Ws.Cell(r365Row, mapping.R365GroupingColumn).Value = "Sysco";
        r365Row++;

        // Case 5: plain ungrouped exact match (blank Grouping cell).
        bankWs.Cell(bankRow, mapping.BankDateColumn).Value = baseDate; bankWs.Cell(bankRow, mapping.BankCreditColumn).Value = 42.50;
        r365Ws.Cell(r365Row, mapping.R365DateColumn).Value = baseDate; r365Ws.Cell(r365Row, mapping.R365AmountColumn).Value = 42.50;

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

        // Bank rows, in write order (data starts at mapping.BankDataStartRow):
        // 5=group10, 6=group6, 7=Sysco, 8=plain.
        Assert.False(outputBankWs.Cell(5, mapping.BankConfidenceColumn).IsEmpty()); // group-10 bank row got a Match ID
        Assert.Contains("Grouping", outputBankWs.Cell(6, mapping.BankCommentColumn).GetString()); // group-6 bank row's comment mentions Grouping
        Assert.Contains("Named Rule", outputBankWs.Cell(7, mapping.BankCommentColumn).GetString()); // Sysco bank row
        Assert.Equal("Matched (Exact)", outputBankWs.Cell(8, mapping.BankCommentColumn).GetString()); // plain exact-match bank row
        // R365 rows, in write order: 5-7=group10, 8=group6, 9-10=group99, 11=Sysco, 12=plain.
        Assert.False(outputR365Ws.Cell(9, mapping.R365CommentColumn).IsEmpty()); // first group-99 R365 row got a comment written
    }
}
