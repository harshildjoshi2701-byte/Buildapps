using BankReconciliation.Core.Models;

namespace BankReconciliation.Core.Services;

/// <summary>Persists a completed <see cref="ReconciliationLog"/> to disk, and
/// exposes where log files live so the UI's "Open Log" action can find them.</summary>
public interface ILoggingService
{
    /// <summary>Writes both a human-readable .log and a machine-readable .json
    /// version of <paramref name="log"/> into the log directory. Returns the
    /// path of the human-readable file (what "Open Log" should launch).</summary>
    string WriteLog(ReconciliationLog log);

    /// <summary>Directory log files are written to (created if missing).</summary>
    string LogDirectory { get; }

    /// <summary>Most recent log files, newest first.</summary>
    IReadOnlyList<string> GetRecentLogFiles(int max = 20);
}
