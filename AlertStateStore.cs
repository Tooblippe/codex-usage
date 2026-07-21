using System.Text.Json;

namespace CodexUsageTray;

internal sealed class AlertStateStore
{
    private readonly string filePath;
    private readonly Dictionary<string, long> alertedResets;

    /// <summary>
    /// Creates the alert-state store at the standard local application-data path.
    /// </summary>
    /// <param name="filePath">An optional file path used by self-tests.</param>
    internal AlertStateStore(string? filePath = null)
    {
        this.filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexUsageTray",
            "alerts.json");
        alertedResets = Load(this.filePath);
    }

    /// <summary>
    /// Records and reports whether a low-usage alert should be shown for this reset cycle.
    /// </summary>
    /// <param name="windowKey">The stable rate-limit window name.</param>
    /// <param name="reading">The normalized rate-limit reading.</param>
    /// <returns>True once per window and reset timestamp at or below twenty percent.</returns>
    internal bool ShouldAlert(string windowKey, LimitReading reading)
    {
        if (reading.State != LimitState.Available
            || reading.RemainingPercent is null or > 20
            || reading.ResetsAt is null)
        {
            return false;
        }

        long reset = reading.ResetsAt.Value.ToUnixTimeSeconds();
        if (alertedResets.TryGetValue(windowKey, out long alertedReset) && alertedReset == reset)
        {
            return false;
        }

        alertedResets[windowKey] = reset;
        Save();
        return true;
    }

    /// <summary>
    /// Loads persisted reset timestamps, treating missing or invalid state as empty.
    /// </summary>
    /// <param name="path">The alert-state JSON file.</param>
    /// <returns>The persisted reset timestamps.</returns>
    private static Dictionary<string, long> Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Persists the current reset timestamps to local application data.
    /// </summary>
    private void Save()
    {
        try
        {
            string? directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(filePath, JsonSerializer.Serialize(alertedResets));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A notification-state write must not hide otherwise valid usage data.
        }
    }
}
