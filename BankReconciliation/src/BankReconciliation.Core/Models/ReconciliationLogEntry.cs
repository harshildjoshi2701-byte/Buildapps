namespace BankReconciliation.Core.Models;

/// <summary>One line of the reconciliation log.</summary>
public sealed class ReconciliationLogEntry
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public LogLevel Level { get; init; } = LogLevel.Info;
    public required string Message { get; init; }

    public override string ToString() => $"[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level}] {Message}";
}

/// <summary>
/// Full structured log for one reconciliation run: header stats plus every
/// log line, including per-combination details ("Bank row 812 matched to 7
/// R365 rows: 401,402,405,..."). Written to disk by ILoggingService as both a
/// human-readable .log and (optionally) machine-readable .json.
/// </summary>
public sealed class ReconciliationLog
{
    public DateTime RunDate { get; init; } = DateTime.Now;
    public string SourceFile { get; set; } = string.Empty;
    public string OutputFile { get; set; } = string.Empty;
    public int RowsProcessed { get; set; }
    public int RowsMatched { get; set; }
    public TimeSpan ProcessingTime { get; set; }
    public List<ReconciliationLogEntry> Entries { get; } = new();

    public int ErrorCount => Entries.Count(e => e.Level == LogLevel.Error);
    public int WarningCount => Entries.Count(e => e.Level == LogLevel.Warning);

    public void Info(string message) => Entries.Add(new ReconciliationLogEntry { Level = LogLevel.Info, Message = message });
    public void Warn(string message) => Entries.Add(new ReconciliationLogEntry { Level = LogLevel.Warning, Message = message });
    public void Error(string message) => Entries.Add(new ReconciliationLogEntry { Level = LogLevel.Error, Message = message });
}
