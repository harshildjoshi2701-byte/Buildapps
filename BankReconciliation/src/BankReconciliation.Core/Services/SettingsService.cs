using System.Text.Json;
using System.Text.Json.Serialization;
using BankReconciliation.Core.Models;

namespace BankReconciliation.Core.Services;

/// <summary>
/// Persists settings as JSON at <c>%AppData%\BankReconciliationApp\settings.json</c>.
/// A plain JSON file (rather than SQLite) was a deliberate choice for this
/// small, single-user, rarely-written piece of state — see README
/// "Architecture Decisions" for the reasoning and how this could be swapped
/// for a SQLite-backed implementation later without touching any caller,
/// since everything goes through <see cref="ISettingsService"/>.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string SettingsFilePath { get; }

    public SettingsService(string? settingsFilePath = null)
    {
        SettingsFilePath = settingsFilePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BankReconciliationApp", "settings.json");
    }

    public ReconciliationSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
                return new ReconciliationSettings();

            var json = File.ReadAllText(SettingsFilePath);
            var settings = JsonSerializer.Deserialize<ReconciliationSettings>(json, JsonOptions);
            return settings ?? new ReconciliationSettings();
        }
        catch (Exception)
        {
            // A corrupt or unreadable settings file should never prevent the
            // app from starting — fall back to defaults rather than crash.
            return new ReconciliationSettings();
        }
    }

    public void Save(ReconciliationSettings settings)
    {
        var directory = Path.GetDirectoryName(SettingsFilePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsFilePath, json);
    }

    public void AddRecentFile(ReconciliationSettings settings, string filePath)
    {
        settings.RecentFiles.RemoveAll(f => string.Equals(f, filePath, StringComparison.OrdinalIgnoreCase));
        settings.RecentFiles.Insert(0, filePath);
        const int maxRecent = 10;
        if (settings.RecentFiles.Count > maxRecent)
            settings.RecentFiles.RemoveRange(maxRecent, settings.RecentFiles.Count - maxRecent);
        Save(settings);
    }
}
