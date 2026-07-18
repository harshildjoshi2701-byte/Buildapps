using System.Text.Json;
using BankReconciliation.Core.Models;

namespace BankReconciliation.Core.Services;

/// <summary>
/// Writes reconciliation logs to <c>%AppData%\BankReconciliationApp\logs</c>
/// as a timestamped pair of files: a human-readable <c>.log</c> (what a
/// finance user would open) and a machine-readable <c>.json</c> (useful for
/// scripting / audit tooling / support requests). Uses only
/// System.Text.Json (built into .NET) — no extra NuGet dependency.
/// </summary>
public sealed class LoggingService : ILoggingService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public string LogDirectory { get; }

    public LoggingService(string? logDirectory = null)
    {
        LogDirectory = logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BankReconciliationApp", "logs");
        Directory.CreateDirectory(LogDirectory);
    }

    public string WriteLog(ReconciliationLog log)
    {
        var timestamp = log.RunDate.ToString("yyyyMMdd_HHmmss");
        var textPath = Path.Combine(LogDirectory, $"reconciliation_{timestamp}.log");
        var jsonPath = Path.Combine(LogDirectory, $"reconciliation_{timestamp}.json");

        File.WriteAllText(textPath, RenderText(log));
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(ToJsonModel(log), JsonOptions));

        return textPath;
    }

    public IReadOnlyList<string> GetRecentLogFiles(int max = 20)
    {
        if (!Directory.Exists(LogDirectory)) return Array.Empty<string>();
        return Directory.GetFiles(LogDirectory, "reconciliation_*.log")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(max)
            .ToList();
    }

    private static string RenderText(ReconciliationLog log)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("========================================================================");
        sb.AppendLine(" BANK RECONCILIATION LOG");
        sb.AppendLine("========================================================================");
        sb.AppendLine($"Run Date:          {log.RunDate:yyyy-MM-dd}");
        sb.AppendLine($"Run Time:          {log.RunDate:HH:mm:ss}");
        sb.AppendLine($"Source File:       {log.SourceFile}");
        sb.AppendLine($"Output File:       {log.OutputFile}");
        sb.AppendLine($"Rows Processed:    {log.RowsProcessed:N0}");
        sb.AppendLine($"Rows Matched:      {log.RowsMatched:N0}");
        sb.AppendLine($"Processing Time:   {log.ProcessingTime.TotalSeconds:F2}s");
        sb.AppendLine($"Errors:            {log.ErrorCount:N0}");
        sb.AppendLine($"Warnings:          {log.WarningCount:N0}");
        sb.AppendLine("------------------------------------------------------------------------");
        foreach (var entry in log.Entries)
            sb.AppendLine(entry.ToString());
        sb.AppendLine("========================================================================");
        return sb.ToString();
    }

    private static object ToJsonModel(ReconciliationLog log) => new
    {
        log.RunDate,
        log.SourceFile,
        log.OutputFile,
        log.RowsProcessed,
        log.RowsMatched,
        ProcessingTimeSeconds = log.ProcessingTime.TotalSeconds,
        log.ErrorCount,
        log.WarningCount,
        Entries = log.Entries.Select(e => new { e.Timestamp, Level = e.Level.ToString(), e.Message }),
    };
}
