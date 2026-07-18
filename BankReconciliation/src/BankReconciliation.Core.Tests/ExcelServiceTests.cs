using BankReconciliation.Core.Models;
using BankReconciliation.Core.Services;
using ClosedXML.Excel;
using Xunit;

namespace BankReconciliation.Core.Tests;

/// <summary>
/// Integration-style tests that build a real .xlsx on disk with ClosedXML,
/// then drive it through the actual ExcelService — the two-worksheet
/// read/write rewrite had zero test coverage otherwise (everything else
/// this session tested pure in-memory transaction-list logic).
/// </summary>
public class ExcelServiceTests : IDisposable
{
    private readonly string _tempDir;

    public ExcelServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ExcelServiceTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private static ColumnMapping DefaultMapping() => new()
    {
        BankWorksheetName = "Bank Transactions",
        R365WorksheetName = "R365 Transactions",
        BankHeaderRow = 2,
        BankDataStartRow = 3,
        BankFirstColumn = 1,
        BankDateColumn = 3,
        BankCreditColumn = 6,
        BankDebitColumn = 7,
        BankGroupingColumn = 8,
        BankDescriptionColumn = 8,
        BankCommentColumn = 9,
        BankConfidenceColumn = 10,
        BankDiffColumn = 11,
        R365HeaderRow = 2,
        R365DataStartRow = 3,
        R365FirstColumn = 1,
        R365DateColumn = 1,
        R365GroupingColumn = 2,
        R365ReferenceColumn = 3,
        R365DescriptionColumn = 4,
        R365AmountColumn = 5,
        R365CommentColumn = 6,
        R365ConfidenceColumn = 7,
        R365DiffColumn = 8,
    };

    private string BuildWorkbook(Action<IXLWorksheet, IXLWorksheet> populate)
    {
        using var wb = new XLWorkbook();
        var bankWs = wb.Worksheets.Add("Bank Transactions");
        var r365Ws = wb.Worksheets.Add("R365 Transactions");
        populate(bankWs, r365Ws);
        var path = Path.Combine(_tempDir, $"test_{Guid.NewGuid():N}.xlsx");
        wb.SaveAs(path);
        return path;
    }

    [Fact]
    public void Load_ReadsBankAndR365FromSeparateNamedWorksheets()
    {
        var path = BuildWorkbook((bank, r365) =>
        {
            bank.Cell(3, 3).Value = new DateTime(2026, 6, 1);
            bank.Cell(3, 6).Value = 600.00;
            r365.Cell(3, 1).Value = new DateTime(2026, 5, 30);
            r365.Cell(3, 5).Value = 600.00;
        });

        using var loaded = new ExcelService().Load(path, DefaultMapping(), ignoreAlreadyReconciledRows: false);

        Assert.Single(loaded.BankTransactions);
        Assert.Single(loaded.R365Transactions);
        Assert.Equal(60000, loaded.BankTransactions[0].AmountCents);
        Assert.Equal(60000, loaded.R365Transactions[0].AmountCents);
    }

    [Fact]
    public void Load_PopulatesGroupingKeyFromConfiguredColumn_OnBothSheets()
    {
        var path = BuildWorkbook((bank, r365) =>
        {
            bank.Cell(3, 3).Value = new DateTime(2026, 6, 1);
            bank.Cell(3, 6).Value = 600.00;
            bank.Cell(3, 8).Value = "42";
            r365.Cell(3, 1).Value = new DateTime(2026, 5, 30);
            r365.Cell(3, 5).Value = 600.00;
            r365.Cell(3, 2).Value = "42";
        });

        using var loaded = new ExcelService().Load(path, DefaultMapping(), ignoreAlreadyReconciledRows: false);

        Assert.Equal("42", loaded.BankTransactions[0].GroupingKey);
        Assert.Equal("42", loaded.R365Transactions[0].GroupingKey);
    }

    [Fact]
    public void Load_BlankGroupingCell_ReadsAsEmptyString()
    {
        var path = BuildWorkbook((bank, r365) =>
        {
            bank.Cell(3, 3).Value = new DateTime(2026, 6, 1);
            bank.Cell(3, 6).Value = 100.00;
            r365.Cell(3, 1).Value = new DateTime(2026, 6, 1);
            r365.Cell(3, 5).Value = 100.00;
        });

        using var loaded = new ExcelService().Load(path, DefaultMapping(), ignoreAlreadyReconciledRows: false);

        Assert.Equal(string.Empty, loaded.BankTransactions[0].GroupingKey);
        Assert.Equal(string.Empty, loaded.R365Transactions[0].GroupingKey);
    }

    [Fact]
    public void Load_WrongWorksheetName_ThrowsClearErrorListingAvailableSheets()
    {
        var path = BuildWorkbook((bank, r365) => { });
        var mapping = DefaultMapping();
        mapping.BankWorksheetName = "Does Not Exist";

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ExcelService().Load(path, mapping, ignoreAlreadyReconciledRows: false));

        Assert.Contains("Does Not Exist", ex.Message);
        Assert.Contains("Bank Transactions", ex.Message);
        Assert.Contains("R365 Transactions", ex.Message);
    }

    [Fact]
    public void Load_WorksheetNameNotExact_FallsBackToLooseContainsMatch()
    {
        using var wb = new XLWorkbook();
        wb.Worksheets.Add("Bank Transactions (June 2026)");
        wb.Worksheets.Add("R365 Transactions (June 2026)");
        var path = Path.Combine(_tempDir, $"test_{Guid.NewGuid():N}.xlsx");
        wb.SaveAs(path);

        // Configured names ("Bank Transactions" / "R365 Transactions") are a
        // strict substring of the real sheet names, not an exact match — the
        // loose fallback in ExcelService.ResolveWorksheet must still find them.
        using var loaded = new ExcelService().Load(path, DefaultMapping(), ignoreAlreadyReconciledRows: false);

        Assert.Empty(loaded.BankTransactions); // no data rows, but it found the sheet without throwing
        Assert.Empty(loaded.R365Transactions);
    }

    [Fact]
    public void Load_IgnoreAlreadyReconciledRows_PreLocksOnlyRowsWithExistingComment()
    {
        var path = BuildWorkbook((bank, r365) =>
        {
            bank.Cell(3, 3).Value = new DateTime(2026, 6, 1);
            bank.Cell(3, 6).Value = 100.00;
            bank.Cell(3, 9).Value = "Matched (Exact)"; // pre-existing comment
            bank.Cell(4, 3).Value = new DateTime(2026, 6, 2);
            bank.Cell(4, 6).Value = 200.00;
        });

        using var loaded = new ExcelService().Load(path, DefaultMapping(), ignoreAlreadyReconciledRows: true);

        Assert.True(loaded.BankTransactions[0].IsPreLocked);
        Assert.True(loaded.BankTransactions[0].IsMatched);
        Assert.False(loaded.BankTransactions[1].IsPreLocked);
        Assert.False(loaded.BankTransactions[1].IsMatched);
    }

    [Fact]
    public void WriteResultsAndSave_WritesCommentAndMatchId_OnCorrectSheets_AndNeverTouchesTheSourceFile()
    {
        var path = BuildWorkbook((bank, r365) =>
        {
            bank.Cell(3, 3).Value = new DateTime(2026, 6, 1);
            bank.Cell(3, 6).Value = 600.00;
            r365.Cell(3, 1).Value = new DateTime(2026, 5, 30);
            r365.Cell(3, 5).Value = 600.00;
        });
        var sourceBytesBefore = File.ReadAllBytes(path);

        var service = new ExcelService();
        var mapping = DefaultMapping();
        var loaded = service.Load(path, mapping, ignoreAlreadyReconciledRows: false);

        loaded.BankTransactions[0].IsMatched = true;
        loaded.BankTransactions[0].Status = MatchStatus.MatchedExact;
        loaded.BankTransactions[0].Comment = "Matched (Exact)";
        loaded.BankTransactions[0].GroupId = 0;
        loaded.R365Transactions[0].IsMatched = true;
        loaded.R365Transactions[0].Status = MatchStatus.MatchedExact;
        loaded.R365Transactions[0].Comment = "Matched (Exact)";
        loaded.R365Transactions[0].GroupId = 0;

        var outputPath = service.BuildOutputPath(path);
        service.WriteResultsAndSave(loaded, loaded.BankTransactions, loaded.R365Transactions,
            new HighlightColors(), mapping, writeConfidenceColumn: true, highlightFullRow: true,
            highlightOnlyUnmatched: true, tidyColumnsOnOutput: false, outputPath);
        loaded.Dispose();

        Assert.True(File.Exists(outputPath));
        Assert.NotEqual(path, outputPath);
        Assert.Equal(sourceBytesBefore, File.ReadAllBytes(path)); // original untouched byte-for-byte

        using var output = new XLWorkbook(outputPath);
        var bankWs = output.Worksheet("Bank Transactions");
        var r365Ws = output.Worksheet("R365 Transactions");
        Assert.Equal("Matched (Exact)", bankWs.Cell(3, 9).GetString());
        Assert.Equal(1, bankWs.Cell(3, 10).GetValue<int>()); // GroupId 0 -> written 1-based
        Assert.Equal("Matched (Exact)", r365Ws.Cell(3, 6).GetString());
        Assert.Equal(1, r365Ws.Cell(3, 7).GetValue<int>());
    }

    [Fact]
    public void BuildOutputPath_ExistingFile_AutoIncrementsRatherThanOverwriting()
    {
        var sourcePath = Path.Combine(_tempDir, "MyFile.xlsx");
        File.WriteAllText(sourcePath, "placeholder");
        File.WriteAllText(Path.Combine(_tempDir, "MyFile_Reconciled.xlsx"), "placeholder");

        var result = new ExcelService().BuildOutputPath(sourcePath);

        Assert.Equal(Path.Combine(_tempDir, "MyFile_Reconciled (2).xlsx"), result);
    }
}
