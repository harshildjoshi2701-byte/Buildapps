using BankReconciliation.Core.Models;

namespace BankReconciliation.Core.Services;

/// <summary>Loads/saves <see cref="ReconciliationSettings"/> and maintains the
/// recent-files list, persisted between application runs.</summary>
public interface ISettingsService
{
    ReconciliationSettings Load();

    void Save(ReconciliationSettings settings);

    /// <summary>Adds <paramref name="filePath"/> to the front of the recent
    /// files list (de-duplicating), caps the list at 10, and persists.</summary>
    void AddRecentFile(ReconciliationSettings settings, string filePath);

    string SettingsFilePath { get; }
}
